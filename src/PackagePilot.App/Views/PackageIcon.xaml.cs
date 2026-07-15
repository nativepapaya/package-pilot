using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PackagePilot.App.Services;

namespace PackagePilot.App.Views;

public sealed partial class PackageIcon : UserControl
{
    public static readonly DependencyProperty IconUriProperty = DependencyProperty.Register(
        nameof(IconUri),
        typeof(Uri),
        typeof(PackageIcon),
        new PropertyMetadata(null, OnIconUriChanged));

    public static readonly DependencyProperty FallbackGlyphProperty = DependencyProperty.Register(
        nameof(FallbackGlyph),
        typeof(string),
        typeof(PackageIcon),
        new PropertyMetadata("\uE896"));

    private CancellationTokenSource? _loadCancellation;
    private bool _isLoaded;
    private long _loadGeneration;

    public PackageIcon() => InitializeComponent();

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

    private static void OnIconUriChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is PackageIcon icon && icon._isLoaded)
        {
            icon.StartLoad(args.NewValue as Uri);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (IconUri is not null)
        {
            StartLoad(IconUri);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _loadGeneration++;
        CancelPendingLoad();
    }

    private void StartLoad(Uri? sourceUri)
    {
        CancelPendingLoad();
        var generation = ++_loadGeneration;
        if (sourceUri is null)
        {
            TryShowFallback(generation);
            return;
        }

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _loadCancellation = cancellation;
        TryShowFallback(generation);
        _ = LoadAsync(sourceUri, cancellation, token, generation);
    }

    private async Task LoadAsync(
        Uri sourceUri,
        CancellationTokenSource cancellation,
        CancellationToken token,
        long generation)
    {
        try
        {
            var cachedFile = await PackageIconCache.Shared.GetCachedIconFileAsync(
                sourceUri,
                token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(cancellation, generation) || cachedFile is null)
            {
                return;
            }

            using var stream = await cachedFile.OpenAsync(global::Windows.Storage.FileAccessMode.Read);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(cancellation, generation))
            {
                return;
            }

            var bitmap = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = 64
            };
            await bitmap.SetSourceAsync(stream);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(cancellation, generation))
            {
                return;
            }

            try
            {
                IconImage.Source = bitmap;
                IconImage.Visibility = Visibility.Visible;
                FallbackIcon.Visibility = Visibility.Collapsed;
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
            FallbackIcon.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Optional icon state must never tear down the process during navigation.
        }
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
