using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PackagePilot.App.Services;

/// <summary>
/// Downloads package icons from WinGet metadata, validates them as bounded raster images,
/// and stores only canonical PNG output in the app's local cache.
/// </summary>
public sealed class PackageIconCache
{
    private const int MaxRedirects = 3;
    private const int MaxDownloadBytes = 2 * 1024 * 1024;
    private const int MaxCachedBytes = 4 * 1024 * 1024;
    private const uint MaxDimension = 2048;
    private const ulong MaxPixelCount = 4_194_304;
    private const string CacheRootName = "PackagePilot";
    private const string CacheFolderName = "Icons";

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    private readonly ConcurrentDictionary<string, Lazy<Task<StorageFile?>>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StorageFile> _positiveCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _negativeCache = new(StringComparer.Ordinal);

    public PackageIconCache()
        : this(CreateHttpClient())
    {
    }

    internal PackageIconCache(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public static PackageIconCache Shared { get; } = new();

    /// <summary>
    /// Returns a local-cache file only after the remote image or existing cache entry has
    /// passed validation. Invalid, unsupported, or unavailable images return <see langword="null"/>.
    /// </summary>
    public async Task<StorageFile?> GetCachedIconFileAsync(
        Uri? sourceUri,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedRemoteUri(sourceUri))
        {
            return null;
        }

        var normalizedUri = NormalizeUri(sourceUri!);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUri)));
        if (_positiveCache.TryGetValue(cacheKey, out var knownFile))
        {
            return knownFile;
        }

        if (_negativeCache.ContainsKey(cacheKey))
        {
            return null;
        }

        var lazy = _inflight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<StorageFile?>>(
                () => CacheAndReleaseAsync(cacheKey, sourceUri!),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<StorageFile?> CacheAndReleaseAsync(string cacheKey, Uri sourceUri)
    {
        try
        {
            var file = await GetOrCreateCoreAsync(cacheKey, sourceUri).ConfigureAwait(false);
            if (file is null)
            {
                _negativeCache.TryAdd(cacheKey, 0);
            }
            else
            {
                _positiveCache[cacheKey] = file;
            }

            return file;
        }
        finally
        {
            _inflight.TryRemove(cacheKey, out _);
        }
    }

    private async Task<StorageFile?> GetOrCreateCoreAsync(string cacheKey, Uri sourceUri)
    {
        try
        {
            var folder = await GetCacheFolderAsync().ConfigureAwait(false);
            var fileName = $"{cacheKey}.png";
            var existing = await TryGetFileAsync(folder, fileName).ConfigureAwait(false);
            if (existing is not null)
            {
                if (await ValidateCachedPngAsync(existing).ConfigureAwait(false))
                {
                    return existing;
                }

                await TryDeleteAsync(existing).ConfigureAwait(false);
            }

            await _downloadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Another request may have completed while this request waited for the gate.
                existing = await TryGetFileAsync(folder, fileName).ConfigureAwait(false);
                if (existing is not null && await ValidateCachedPngAsync(existing).ConfigureAwait(false))
                {
                    return existing;
                }

                if (existing is not null)
                {
                    await TryDeleteAsync(existing).ConfigureAwait(false);
                }

                var downloaded = await DownloadAsync(sourceUri).ConfigureAwait(false);
                if (downloaded is null)
                {
                    return null;
                }

                var canonicalPng = await DecodeAndEncodePngAsync(
                    downloaded.Value.Bytes,
                    downloaded.Value.Format).ConfigureAwait(false);
                if (canonicalPng is null || canonicalPng.Length > MaxCachedBytes)
                {
                    return null;
                }

                var temporary = await folder.CreateFileAsync(
                    $"{cacheKey}.{Guid.NewGuid():N}.tmp",
                    CreationCollisionOption.FailIfExists).AsTask().ConfigureAwait(false);
                var renamed = false;
                try
                {
                    await FileIO.WriteBytesAsync(temporary, canonicalPng).AsTask().ConfigureAwait(false);
                    await temporary.RenameAsync(fileName, NameCollisionOption.ReplaceExisting).AsTask().ConfigureAwait(false);
                    renamed = true;
                }
                finally
                {
                    if (!renamed)
                    {
                        await TryDeleteAsync(temporary).ConfigureAwait(false);
                    }
                }

                return temporary;
            }
            finally
            {
                _downloadGate.Release();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Package icons are optional. Any validation, decoder, storage, or network
            // failure must leave the caller on the built-in glyph fallback.
            return null;
        }
    }

    private async Task<DownloadedIcon?> DownloadAsync(Uri initialUri)
    {
        var currentUri = initialUri;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            if (!IsAllowedRemoteUri(currentUri))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*", 0.8));

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaxRedirects || response.Headers.Location is null)
                {
                    return null;
                }

                currentUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || !IsAllowedImageMediaType(mediaType))
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            {
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await responseStream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaxDownloadBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            if (!TryDetectFormat(bytes, out var format) || !MediaTypeMatches(mediaType, format))
            {
                return null;
            }

            return new DownloadedIcon(bytes, format);
        }

        return null;
    }

    private static async Task<byte[]?> DecodeAndEncodePngAsync(byte[] bytes, RasterFormat expectedFormat)
    {
        if (!TryDetectFormat(bytes, out var detectedFormat) || detectedFormat != expectedFormat)
        {
            return null;
        }

        using var input = await CreateRandomAccessStreamAsync(bytes).ConfigureAwait(false);
        var decoder = await BitmapDecoder.CreateAsync(input);
        if (!IsAllowedDimensions(decoder.PixelWidth, decoder.PixelHeight))
        {
            return null;
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        if (output.Size == 0 || output.Size > MaxCachedBytes)
        {
            return null;
        }

        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        var length = checked((uint)output.Size);
        await reader.LoadAsync(length);
        var canonicalPng = new byte[length];
        reader.ReadBytes(canonicalPng);
        return TryDetectFormat(canonicalPng, out var format) && format == RasterFormat.Png
            ? canonicalPng
            : null;
    }

    private static async Task<bool> ValidateCachedPngAsync(StorageFile file)
    {
        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size == 0 || properties.Size > MaxCachedBytes)
            {
                return false;
            }

            var buffer = await FileIO.ReadBufferAsync(file);
            using var reader = DataReader.FromBuffer(buffer);
            var bytes = new byte[buffer.Length];
            reader.ReadBytes(bytes);
            if (!TryDetectFormat(bytes, out var format) || format != RasterFormat.Png)
            {
                return false;
            }

            using var stream = await CreateRandomAccessStreamAsync(bytes).ConfigureAwait(false);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (!IsAllowedDimensions(decoder.PixelWidth, decoder.PixelHeight))
            {
                return false;
            }

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            return bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static async Task<InMemoryRandomAccessStream> CreateRandomAccessStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);
        return stream;
    }

    private static async Task<StorageFolder> GetCacheFolderAsync()
    {
        var root = await ApplicationData.Current.LocalCacheFolder.CreateFolderAsync(
            CacheRootName,
            CreationCollisionOption.OpenIfExists);
        return await root.CreateFolderAsync(CacheFolderName, CreationCollisionOption.OpenIfExists);
    }

    private static async Task<StorageFile?> TryGetFileAsync(StorageFolder folder, string fileName)
    {
        try
        {
            return await folder.GetFileAsync(fileName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static async Task TryDeleteAsync(StorageFile file)
    {
        try
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Cache cleanup is best effort.
        }
    }

    private static bool IsAllowedRemoteUri(Uri? uri) =>
        uri is
        {
            IsAbsoluteUri: true,
            Scheme: "https",
            Host: not "",
            UserInfo: ""
        };

    private static string NormalizeUri(Uri uri) =>
        uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);

    private static bool IsAllowedDimensions(uint width, uint height) =>
        width is > 0 and <= MaxDimension &&
        height is > 0 and <= MaxDimension &&
        (ulong)width * height <= MaxPixelCount;

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsAllowedImageMediaType(string mediaType) => mediaType.ToLowerInvariant() is
        "image/png" or
        "image/x-png" or
        "image/jpeg" or
        "image/jpg" or
        "image/gif" or
        "image/bmp" or
        "image/x-ms-bmp" or
        "image/x-icon" or
        "image/vnd.microsoft.icon" or
        "image/tiff" or
        "image/webp";

    private static bool MediaTypeMatches(string mediaType, RasterFormat format)
    {
        var normalized = mediaType.ToLowerInvariant();
        return format switch
        {
            RasterFormat.Png => normalized is "image/png" or "image/x-png",
            RasterFormat.Jpeg => normalized is "image/jpeg" or "image/jpg",
            RasterFormat.Gif => normalized == "image/gif",
            RasterFormat.Bmp => normalized is "image/bmp" or "image/x-ms-bmp",
            RasterFormat.Ico => normalized is "image/x-icon" or "image/vnd.microsoft.icon",
            RasterFormat.Tiff => normalized == "image/tiff",
            RasterFormat.WebP => normalized == "image/webp",
            _ => false
        };
    }

    private static bool TryDetectFormat(ReadOnlySpan<byte> bytes, out RasterFormat format)
    {
        if (bytes.Length >= 8 &&
            bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            format = RasterFormat.Png;
            return true;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            format = RasterFormat.Jpeg;
            return true;
        }

        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            format = RasterFormat.Gif;
            return true;
        }

        if (bytes.StartsWith("BM"u8))
        {
            format = RasterFormat.Bmp;
            return true;
        }

        if (bytes.Length >= 4 &&
            bytes[..4].SequenceEqual(new byte[] { 0x00, 0x00, 0x01, 0x00 }))
        {
            format = RasterFormat.Ico;
            return true;
        }

        if (bytes.Length >= 4 &&
            (bytes[..4].SequenceEqual(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) ||
             bytes[..4].SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })))
        {
            format = RasterFormat.Tiff;
            return true;
        }

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            format = RasterFormat.WebP;
            return true;
        }

        format = default;
        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseDefaultCredentials = false
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PackagePilot/1.0");
        return client;
    }

    private readonly record struct DownloadedIcon(byte[] Bytes, RasterFormat Format);

    private enum RasterFormat
    {
        Png,
        Jpeg,
        Gif,
        Bmp,
        Ico,
        Tiff,
        WebP
    }
}
