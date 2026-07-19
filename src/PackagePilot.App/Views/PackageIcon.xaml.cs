using System.Collections.Concurrent;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PackagePilot.App.Services;
using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PackagePilot.App.Views;

public sealed partial class PackageIcon : UserControl
{
    private const int MaximumDecodedIconEntries = 512;

    public static readonly DependencyProperty IconUriProperty = DependencyProperty.Register(
        nameof(IconUri),
        typeof(Uri),
        typeof(PackageIcon),
        new PropertyMetadata(null, OnIconUriChanged));

    public static readonly DependencyProperty FallbackGlyphProperty = DependencyProperty.Register(
        nameof(FallbackGlyph),
        typeof(string),
        typeof(PackageIcon),
        new PropertyMetadata("\uE896", OnFallbackVisualChanged));
    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(
        nameof(FallbackText),
        typeof(string),
        typeof(PackageIcon),
        new PropertyMetadata(string.Empty, OnFallbackVisualChanged));
    public static readonly DependencyProperty IconReferenceProperty = DependencyProperty.Register(
        nameof(IconReference),
        typeof(AppIconReference),
        typeof(PackageIcon),
        new PropertyMetadata(null, OnIconReferenceChanged));

    private static readonly ConcurrentDictionary<string, StorageFile> LocalPositiveCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> LocalNegativeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, WeakReference<BitmapImage>> DecodedImageCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> DecodedImageCacheOrder = new();

    private CancellationTokenSource? _loadCancellation;
    private bool _isLoaded;
    private long _loadGeneration;

    public PackageIcon()
    {
        InitializeComponent();
        UpdateFallbackVisual();
    }

    public Uri? IconUri
    {
        get => (Uri?)GetValue(IconUriProperty);
        set => SetValue(IconUriProperty, value);
    }

    public string FallbackGlyph
    {
        get => (string)GetValue(FallbackGlyphProperty);
        set => SetValue(FallbackGlyphProperty, value);
    }

    public string FallbackText
    {
        get => (string)GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    public AppIconReference? IconReference
    {
        get => (AppIconReference?)GetValue(IconReferenceProperty);
        set => SetValue(IconReferenceProperty, value);
    }

    private static void OnIconUriChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is PackageIcon icon && icon._isLoaded)
        {
            icon.StartLoad(args.NewValue as Uri, icon.IconReference);
        }
    }

    private static void OnIconReferenceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is PackageIcon icon && icon._isLoaded)
        {
            icon.StartLoad(icon.IconUri, args.NewValue as AppIconReference);
        }
    }

    private static void OnFallbackVisualChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is PackageIcon icon && icon.IconImage is not null
            && icon.IconImage.Visibility != Visibility.Visible)
        {
            icon.UpdateFallbackVisual();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (IconUri is not null || IconReference is not null)
        {
            StartLoad(IconUri, IconReference);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _loadGeneration++;
        CancelPendingLoad();
    }

    private void StartLoad(Uri? sourceUri, AppIconReference? iconReference)
    {
        CancelPendingLoad();
        var generation = ++_loadGeneration;
        if (sourceUri is null && iconReference is null)
        {
            TryShowFallback(generation);
            return;
        }

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _loadCancellation = cancellation;
        TryShowFallback(generation);
        _ = LoadAsync(sourceUri, iconReference, cancellation, token, generation);
    }

    private async Task LoadAsync(
        Uri? sourceUri,
        AppIconReference? iconReference,
        CancellationTokenSource cancellation,
        CancellationToken token,
        long generation)
    {
        try
        {
            var bitmap = iconReference is
                { Kind: AppIconSourceKind.ValidatedExecutableResource }
                    ? await LoadExecutableBitmapAsync(iconReference, token)
                    : await LoadFileBitmapAsync(sourceUri, iconReference, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(cancellation, generation) || bitmap is null)
            {
                return;
            }

            try
            {
                IconImage.Source = bitmap;
                IconImage.Visibility = Visibility.Visible;
                FallbackIcon.Visibility = Visibility.Collapsed;
                FallbackMonogram.Visibility = Visibility.Collapsed;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The visual may have detached between the final guard and the XAML
                // property writes. The glyph was already visible before loading, so a
                // failed optional icon application needs no compensating XAML calls.
            }
        }
        catch (OperationCanceledException)
        {
            // Recycled rows and unloaded pages should not change their visual state.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryShowFallback(cancellation, generation);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private static async Task<BitmapImage?> LoadFileBitmapAsync(
        Uri? sourceUri,
        AppIconReference? iconReference,
        CancellationToken cancellationToken)
    {
        var cachedFile = iconReference is
            { Kind: AppIconSourceKind.MsixPackageAsset or AppIconSourceKind.ValidatedLocalResource }
                ? await GetValidatedLocalFileAsync(iconReference, cancellationToken)
                : await PackageIconCache.Shared.GetCachedIconFileAsync(
                    iconReference?.Uri ?? sourceUri,
                    cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (cachedFile is null)
        {
            return null;
        }

        return await LoadAndCacheBitmapAsync(
            $"file|{cachedFile.Path}",
            async () => await cachedFile.OpenAsync(global::Windows.Storage.FileAccessMode.Read),
            cancellationToken);
    }

    private static async Task<BitmapImage?> LoadExecutableBitmapAsync(
        AppIconReference iconReference,
        CancellationToken cancellationToken)
    {
        var bytes = await WindowsExecutableIconExtractor.Shared.GetIconPngAsync(
            iconReference,
            64,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes is null)
        {
            return null;
        }

        var key = $"exe|{iconReference.ResourcePath}|{iconReference.ResourceIndex ?? 0}";
        return await LoadAndCacheBitmapAsync(
            key,
            async () =>
            {
                var stream = new InMemoryRandomAccessStream();
                using var writer = new DataWriter(stream);
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
                stream.Seek(0);
                return stream;
            },
            cancellationToken);
    }

    private static async Task<BitmapImage?> LoadAndCacheBitmapAsync(
        string key,
        Func<Task<IRandomAccessStream>> openStreamAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DecodedImageCache.TryGetValue(key, out var weak)
            && weak.TryGetTarget(out var cached))
        {
            return cached;
        }

        using var stream = await openStreamAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = CreateBitmap();
        await bitmap.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        CacheDecodedBitmap(key, bitmap);
        return bitmap;
    }

    private static void CacheDecodedBitmap(string key, BitmapImage bitmap)
    {
        var weak = new WeakReference<BitmapImage>(bitmap);
        if (DecodedImageCache.TryAdd(key, weak))
        {
            DecodedImageCacheOrder.Enqueue(key);
        }
        else
        {
            DecodedImageCache[key] = weak;
        }

        while (DecodedImageCache.Count > MaximumDecodedIconEntries
               && DecodedImageCacheOrder.TryDequeue(out var oldest))
        {
            DecodedImageCache.TryRemove(oldest, out _);
        }
    }

    private static BitmapImage CreateBitmap() => new()
    {
        DecodePixelType = DecodePixelType.Logical,
        DecodePixelWidth = 64
    };

    private static async Task<StorageFile?> GetValidatedLocalFileAsync(
        AppIconReference reference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference.ResourcePath))
        {
            return null;
        }

        var key = reference.ResourcePath;
        if (LocalPositiveCache.TryGetValue(key, out var known))
        {
            return known;
        }

        if (LocalNegativeCache.ContainsKey(key))
        {
            return null;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(key).AsTask(cancellationToken);
            var properties = await file.GetBasicPropertiesAsync().AsTask(cancellationToken);
            if (!AppIconReferencePolicy.TryNormalizeLocalPath(
                    key,
                    checked((long)properties.Size),
                    out var normalized)
                || !string.Equals(normalized, key, StringComparison.OrdinalIgnoreCase))
            {
                LocalNegativeCache.TryAdd(key, 0);
                return null;
            }

            LocalPositiveCache[key] = file;
            return file;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LocalNegativeCache.TryAdd(key, 0);
            return null;
        }
    }

    private bool IsCurrent(CancellationTokenSource cancellation, long generation) =>
        _isLoaded &&
        generation == _loadGeneration &&
        ReferenceEquals(_loadCancellation, cancellation) &&
        !cancellation.IsCancellationRequested;

    private void TryShowFallback(CancellationTokenSource cancellation, long generation)
    {
        if (IsCurrent(cancellation, generation))
        {
            TryShowFallback(generation);
        }
    }

    private void TryShowFallback(long generation)
    {
        if (!_isLoaded || generation != _loadGeneration)
        {
            return;
        }

        try
        {
            IconImage.Source = null;
            IconImage.Visibility = Visibility.Collapsed;
            UpdateFallbackVisual();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Optional icon state must never tear down the process during navigation.
        }
    }

    private void UpdateFallbackVisual()
    {
        var monogram = CreateMonogram(FallbackText);
        FallbackMonogram.Text = monogram;
        FallbackMonogram.Visibility = string.IsNullOrEmpty(monogram)
            ? Visibility.Collapsed
            : Visibility.Visible;
        FallbackIcon.Visibility = string.IsNullOrEmpty(monogram)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal static string CreateMonogram(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                return Rune.ToUpperInvariant(rune).ToString();
            }
        }

        return string.Empty;
    }

    private void CancelPendingLoad()
    {
        if (_loadCancellation is null)
        {
            return;
        }

        var cancellation = _loadCancellation;
        _loadCancellation = null;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The load owns disposal; completion can race a recycle notification.
        }
    }
}
