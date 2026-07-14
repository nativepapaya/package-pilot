using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PackagePilot.App.Services;
using PackagePilot.App.ViewModels;
using PackagePilot.Core.Services;
using Windows.Storage;

namespace PackagePilot.App;

/// <summary>Creates the application services and owns their lifetime.</summary>
public partial class App : Application
{
    private MainWindow? _window;
    private ShellViewModel? _shellViewModel;
    private OperationQueue? _operationQueue;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var wingetClient = new WingetClient();
        var historyPath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "operation-history.json");

        _operationQueue = new OperationQueue(
            wingetClient,
            new JsonOperationHistoryStore(historyPath));
        _shellViewModel = new ShellViewModel(
            wingetClient,
            _operationQueue,
            DispatcherQueue.GetForCurrentThread());

        _window = new MainWindow(_shellViewModel);
        _window.Closed += OnWindowClosed;
        _window.Activate();
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
        }

        _shellViewModel?.Dispose();
        if (_operationQueue is not null)
        {
            await _operationQueue.DisposeAsync();
        }

        _shellViewModel = null;
        _operationQueue = null;
        _window = null;
    }
}
