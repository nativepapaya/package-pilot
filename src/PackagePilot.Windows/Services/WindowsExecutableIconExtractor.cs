using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PackagePilot.Core.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Reads an icon resource from a validated local executable or DLL and converts it to a bounded
/// PNG in memory. The referenced binary is never launched, loaded as application code, or passed
/// through the Windows shell.
/// </summary>
public sealed class WindowsExecutableIconExtractor
{
    private const int MaximumCacheEntries = 512;
    private const int MinimumIconSize = 16;
    private const int MaximumIconSize = 256;
    private const int MaximumEncodedBytes = 1024 * 1024;

    private readonly ConcurrentDictionary<IconCacheKey, Lazy<Task<byte[]?>>> _cache = new();
    private readonly ConcurrentQueue<IconCacheKey> _cacheOrder = new();
    private readonly SemaphoreSlim _extractionGate = new(4, 4);

    public static WindowsExecutableIconExtractor Shared { get; } = new();

    public async Task<byte[]?> GetIconPngAsync(
        AppIconReference? reference,
        int requestedSize,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()
            || !WindowsDisplayIconReferenceParser.TryValidateExecutableReference(
                reference,
                out var path,
                out var resourceIndex,
                out var length,
                out var lastWriteTimeUtc))
        {
            return null;
        }

        var size = Math.Clamp(requestedSize, MinimumIconSize, MaximumIconSize);
        var key = new IconCacheKey(
            path,
            resourceIndex,
            size,
            length,
            lastWriteTimeUtc.Ticks);
        var created = new Lazy<Task<byte[]?>>(
            () => ExtractAndEncodeWithGateAsync(path, resourceIndex, size),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var lazy = _cache.GetOrAdd(key, created);
        if (ReferenceEquals(lazy, created))
        {
            _cacheOrder.Enqueue(key);
            TrimCache();
        }

        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> ExtractAndEncodeWithGateAsync(
        string path,
        int resourceIndex,
        int size)
    {
        await _extractionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ExtractAndEncodeAsync(path, resourceIndex, size).ConfigureAwait(false);
        }
        finally
        {
            _extractionGate.Release();
        }
    }

    private void TrimCache()
    {
        while (_cache.Count > MaximumCacheEntries && _cacheOrder.TryDequeue(out var oldest))
        {
            _cache.TryRemove(oldest, out _);
        }
    }

    private static async Task<byte[]?> ExtractAndEncodeAsync(
        string path,
        int resourceIndex,
        int size)
    {
        try
        {
            var pixels = await Task.Run(() => TryExtractPixels(path, resourceIndex, size))
                .ConfigureAwait(false);
            if (pixels is null)
            {
                return null;
            }

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                checked((uint)pixels.Value.Width),
                checked((uint)pixels.Value.Height),
                96,
                96,
                pixels.Value.Bytes);
            await encoder.FlushAsync();
            if (output.Size is 0 or > MaximumEncodedBytes)
            {
                return null;
            }

            output.Seek(0);
            using var reader = new DataReader(output.GetInputStreamAt(0));
            var length = checked((uint)output.Size);
            await reader.LoadAsync(length);
            var encoded = new byte[length];
            reader.ReadBytes(encoded);
            return encoded;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static ExtractedPixels? TryExtractPixels(string path, int resourceIndex, int size)
    {
        nint icon = 0;
        try
        {
            var extracted = PrivateExtractIconsW(
                path,
                resourceIndex,
                size,
                size,
                out icon,
                out _,
                1,
                0);
            if (extracted == 0 || extracted == uint.MaxValue || icon == 0)
            {
                return null;
            }

            return TryReadIconPixels(icon);
        }
        finally
        {
            if (icon != 0)
            {
                _ = DestroyIcon(icon);
            }
        }
    }

    private static ExtractedPixels? TryReadIconPixels(nint icon)
    {
        if (!GetIconInfo(icon, out var info))
        {
            return null;
        }

        try
        {
            if (info.ColorBitmap == 0)
            {
                return null;
            }

            var bitmap = new Bitmap();
            if (GetObjectW(info.ColorBitmap, Marshal.SizeOf<Bitmap>(), ref bitmap) == 0
                || bitmap.Width is <= 0 or > MaximumIconSize
                || bitmap.Height is <= 0 or > MaximumIconSize)
            {
                return null;
            }

            var byteCount = checked(bitmap.Width * bitmap.Height * 4);
            var pixels = new byte[byteCount];
            if (!TryReadBitmap(info.ColorBitmap, bitmap.Width, bitmap.Height, pixels))
            {
                return null;
            }

            if (!HasAlpha(pixels))
            {
                ApplyMaskAlpha(info.MaskBitmap, bitmap.Width, bitmap.Height, pixels);
            }

            return new ExtractedPixels(bitmap.Width, bitmap.Height, pixels);
        }
        finally
        {
            if (info.ColorBitmap != 0)
            {
                _ = DeleteObject(info.ColorBitmap);
            }

            if (info.MaskBitmap != 0)
            {
                _ = DeleteObject(info.MaskBitmap);
            }
        }
    }

    private static bool TryReadBitmap(nint bitmap, int width, int height, byte[] destination)
    {
        var screen = GetDC(0);
        if (screen == 0)
        {
            return false;
        }

        try
        {
            var info = CreateBitmapInfo(width, height);
            return GetDIBits(
                    screen,
                    bitmap,
                    0,
                    checked((uint)height),
                    destination,
                    ref info,
                    0) == height;
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }
    }

    private static void ApplyMaskAlpha(nint maskBitmap, int width, int height, byte[] pixels)
    {
        var mask = new byte[pixels.Length];
        if (maskBitmap == 0 || !TryReadBitmap(maskBitmap, width, height, mask))
        {
            SetOpaqueAlpha(pixels);
            return;
        }

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var transparent = mask[offset] != 0
                || mask[offset + 1] != 0
                || mask[offset + 2] != 0;
            pixels[offset + 3] = transparent ? (byte)0 : (byte)255;
        }
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void SetOpaqueAlpha(byte[] pixels)
    {
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 255;
        }
    }

    private static BitmapInfo CreateBitmapInfo(int width, int height) => new()
    {
        Header = new BitmapInfoHeader
        {
            Size = checked((uint)Marshal.SizeOf<BitmapInfoHeader>()),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0
        }
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIconsW(
        string fileName,
        int iconIndex,
        int iconWidth,
        int iconHeight,
        out nint icon,
        out uint iconId,
        uint iconCount,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetObjectW(nint value, int size, ref Bitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        nint deviceContext,
        nint bitmap,
        uint start,
        uint lines,
        [Out] byte[] bits,
        ref BitmapInfo info,
        uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RgbQuad
    {
        public byte Blue;
        public byte Green;
        public byte Red;
        public byte Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public RgbQuad Colors;
    }

    private readonly record struct ExtractedPixels(int Width, int Height, byte[] Bytes);

    private readonly record struct IconCacheKey(
        string Path,
        int ResourceIndex,
        int Size,
        long FileLength,
        long LastWriteTimeUtcTicks);
}
