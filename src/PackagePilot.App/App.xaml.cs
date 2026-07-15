using System.Collections.Specialized;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PackagePilot.App.Services;
using PackagePilot.App.ViewModels;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using PackagePilot.Windows.Services;
using Windows.Storage;

namespace PackagePilot.App;

/// <summary>Creates the application services and owns their lifetime.</summary>
public partial class App : Application
{
    private const string DevelopmentPackageName = "PackagePilot.Desktop";
    private const string DevelopmentPublisher = "CN=PackagePilot.Dev";
    private MainWindow? _window;
    private ShellViewModel? _shellViewModel;
    private OperationQueue? _operationQueue;
    private AppInstance? _mainInstance;
    private AppNotificationManager? _notificationManager;
    private DispatcherQueue? _dispatcherQueue;
    private readonly WindowsActivationAdapter _activationAdapter = new();
    private WindowsUpdateNotificationService? _updateNotificationService;
    private AppActivationRequest? _pendingRedirectedActivation;
    private bool _badgeUpdateScheduled;
    private bool _notificationRegistrationSupported;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var registeredInstance = AppInstance.FindOrRegisterForKey(
            WindowsIntegrationConstants.MainInstanceKey);
        if (!registeredInstance.IsCurrent)
        {
            await registeredInstance.RedirectActivationToAsync(activation);
            Environment.Exit(0);
            return;
        }

        _mainInstance = registeredInstance;
        _mainInstance.Activated += OnAppInstanceActivated;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        var migrationNotice = await RunIdentityMigrationAsync();
        RegisterNotifications();

        var wingetClient = new WingetClient();
        var updateCoordinator = new UpdateCoordinator(
            wingetClient,
            new JsonUpdateSnapshotStore(Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "update-snapshot.json")));
        IUpdateNotificationSink notificationSink = _updateNotificationService is { } service
            ? service
            : new NullUpdateNotificationSink();
        var updateScanWorker = new UpdateScanWorker(
            updateCoordinator,
            new UpdateNotificationPolicy(),
            notificationSink);
        var historyPath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "operation-history.json");

        _operationQueue = new OperationQueue(
            wingetClient,
            new JsonOperationHistoryStore(historyPath),
            msixClient: new WindowsMsixPackageOperationClient());
        var installedAppInventory = new InstalledAppInventory(
        [
            new WingetInstalledAppProvider(wingetClient, new WindowsWingetInstalledAliasResolver()),
            new MsixInstalledAppProvider(new WindowsMsixPackageReader()),
            new RegistryInstalledAppProvider(new WindowsRegistryUninstallReader())
        ]);
        _shellViewModel = new ShellViewModel(
            wingetClient,
            _operationQueue,
            _dispatcherQueue,
            updateCoordinator: updateCoordinator,
            updateScanWorker: updateScanWorker,
            installedAppInventory: installedAppInventory,
            sourceManagementService: wingetClient,
            notificationRegistrationSupported: _notificationRegistrationSupported,
            notificationRegistered: _notificationManager is not null,
            backgroundMonitoringState: new WindowsBackgroundUpdateRegistrationService()
                .GetCurrent().State);
        _shellViewModel.AvailableUpdates.CollectionChanged += OnAvailableUpdatesChanged;
        if (!string.IsNullOrWhiteSpace(migrationNotice))
        {
            _shellViewModel.SetStatusMessage(migrationNotice);
        }

        _window = new MainWindow(
            _shellViewModel,
            new ElevatedSourceManagementBroker());
        _window.Closed += OnWindowClosed;
        _window.Activate();

        var parsed = _activationAdapter.Parse(activation);
        if (parsed.Request is { } request)
        {
            await _window.MainPage.ActivateAsync(request);
        }
        else if (!string.IsNullOrWhiteSpace(parsed.Error))
        {
            _shellViewModel.SetStatusMessage($"External activation was rejected: {parsed.Error}");
        }

        if (_pendingRedirectedActivation is { } pending)
        {
            _pendingRedirectedActivation = null;
            await _window.MainPage.ActivateAsync(pending);
        }

        _ = new WindowsJumpListService().ConfigureAsync();
        _ = EnsureBackgroundMonitoringAsync();
    }

    private static async Task<string?> RunIdentityMigrationAsync()
    {
        (string Name, string Publisher) packageIdentity;
        try
        {
            var packageId = global::Windows.ApplicationModel.Package.Current.Id;
            packageIdentity = (packageId.Name, packageId.Publisher);
        }
        catch (InvalidOperationException)
        {
            // Unpackaged developer launches have no stable package identity and must not
            // consume or produce the production migration handoff.
            return null;
        }

        try
        {
            var historyPath = Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "operation-history.json");
            var migration = new IdentityMigrationService(
                IdentityMigrationPaths.GetNeutralFilePath(),
                $"{packageIdentity.Name}|{packageIdentity.Publisher}",
                new WindowsLocalSettingsMigrationStore(),
                historyPath);

            if (string.Equals(packageIdentity.Name, DevelopmentPackageName, StringComparison.Ordinal)
                && string.Equals(packageIdentity.Publisher, DevelopmentPublisher, StringComparison.Ordinal))
            {
                await migration.ExportAsync();
                return null;
            }

            var result = await migration.ImportOnceAsync();
            return result.Outcome == IdentityMigrationImportOutcome.Imported
                ? "Settings and operation history were migrated from the development package."
                : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // Import failures deliberately leave the neutral handoff in place so the next
            // packaged launch can retry without blocking access to the app.
            return $"Identity migration will retry on the next launch (0x{exception.HResult:X8}).";
        }
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        _dispatcherQueue?.TryEnqueue(() => ActivateFromWindowsAsync(args));
    }

    private async void ActivateFromWindowsAsync(AppActivationArguments args)
    {
        var parsed = _activationAdapter.Parse(args);
        if (parsed.Request is { } request && _window is not null)
        {
            _window.Activate();
            await _window.MainPage.ActivateAsync(request);
        }
        else if (parsed.Request is { } pending)
        {
            _pendingRedirectedActivation = pending;
        }
        else if (!string.IsNullOrWhiteSpace(parsed.Error))
        {
            _shellViewModel?.SetStatusMessage($"External activation was rejected: {parsed.Error}");
        }
    }

    private void RegisterNotifications()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            _notificationRegistrationSupported = AppNotificationManager.IsSupported();
            if (!_notificationRegistrationSupported)
            {
                return;
            }

            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _notificationManager = manager;
            _updateNotificationService = new WindowsUpdateNotificationService(manager);
        }
        catch
        {
            // Notification registration can be disabled by Windows or missing in a
            // development package. Update discovery remains fully usable in-app.
        }
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        _dispatcherQueue?.TryEnqueue(async () =>
        {
            var parsed = _activationAdapter.ParseNotification(args);
            if (parsed.Request is { } request && _window is not null)
            {
                _window.Activate();
                await _window.MainPage.ActivateAsync(request);
            }
            else if (parsed.Request is { } pending)
            {
                _pendingRedirectedActivation = pending;
            }
        });
    }

    private void OnAvailableUpdatesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_badgeUpdateScheduled)
        {
            return;
        }

        _badgeUpdateScheduled = true;
        _dispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _badgeUpdateScheduled = false;
            if (_shellViewModel is null)
            {
                return;
            }

            WindowsUpdateNotificationService.SetBadgeCount(
                _shellViewModel.AvailableUpdates.Count);
        });
    }

    private static UpdateMonitoringCadence ReadMonitoringCadence()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        var value = values.TryGetValue("updateMonitoringCadence", out var stored)
            ? stored as string
            : null;
        return value switch
        {
            "manual" => UpdateMonitoringCadence.Manual,
            "sixHours" => UpdateMonitoringCadence.EverySixHours,
            _ => UpdateMonitoringCadence.Daily
        };
    }

    private async Task EnsureBackgroundMonitoringAsync()
    {
        var cadence = ReadMonitoringCadence();
        var registration = new WindowsBackgroundUpdateRegistrationService();
        var current = registration.GetCurrent();
        BackgroundMonitoringResult result = current;
        if ((cadence == UpdateMonitoringCadence.Manual && current.State != BackgroundMonitoringState.Disabled)
            || (cadence != UpdateMonitoringCadence.Manual && current.State != BackgroundMonitoringState.Registered))
        {
            result = await registration.ConfigureAsync(cadence);
        }

        _shellViewModel?.SetBackgroundMonitoringState(result.State);
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
        }

        if (_shellViewModel is not null)
        {
            _shellViewModel.AvailableUpdates.CollectionChanged -= OnAvailableUpdatesChanged;
        }

        if (_notificationManager is not null)
        {
            _notificationManager.NotificationInvoked -= OnNotificationInvoked;
            _notificationManager.Unregister();
            _notificationManager = null;
        }

        if (_mainInstance is not null)
        {
            _mainInstance.Activated -= OnAppInstanceActivated;
            _mainInstance = null;
        }

        _shellViewModel?.Dispose();
        if (_operationQueue is not null)
        {
            await _operationQueue.DisposeAsync();
        }

        _shellViewModel = null;
        _operationQueue = null;
        _window = null;
        _updateNotificationService = null;
        _dispatcherQueue = null;
    }

    private sealed class NullUpdateNotificationSink : IUpdateNotificationSink
    {
        public Task ApplyAsync(
            UpdateNotificationDecision decision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
