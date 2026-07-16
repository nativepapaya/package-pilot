using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using PackagePilot.App.Services;
using PackagePilot.App.ViewModels;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using PackagePilot.Windows.Services;
using Windows.Storage;

namespace PackagePilot.App;

/// <summary>Creates resident shell services and lazily owns the foreground service graph.</summary>
public partial class App : Application, IAppLifetimeController
{
    private readonly WindowsActivationAdapter _activationAdapter = new();
    private readonly IAppBehaviorSettingsStore _behaviorSettingsStore =
        new WindowsAppBehaviorSettingsStore();
    private readonly IStartupRegistrationService _startupRegistration =
        new WindowsStartupRegistrationService();
    private readonly WindowActivityService _windowActivity = new();
    private readonly IAppLifetimeActivityGate _lifetimeActivityGate =
        new AppLifetimeActivityGate();
    private readonly SemaphoreSlim _lifetimeTransitionGate = new(1, 1);
    private readonly CancellationTokenSource _residentLifetime = new();

    private MainWindow? _window;
    private ShellViewModel? _shellViewModel;
    private OperationQueue? _operationQueue;
    private AppNotificationManager? _notificationManager;
    private DispatcherQueue? _dispatcherQueue;
    private WindowsUpdateNotificationService? _updateNotificationService;
    private INotificationAreaIconService? _notificationAreaIcon;
    private Task? _residentInitialization;
    private Task? _shutdown;
    private bool _badgeUpdateScheduled;
    private bool _notificationRegistrationSupported;
    private bool _jumpListConfigured;
    private bool _isExiting;
    private bool _systemShutdownRequested;
    private int _lifetimeTransitionCount;
    private int _lastKnownUpdateCount;
    private int _foregroundActivationRequested;
    private int _redirectedActivationDispatchCount;

    public App()
    {
        InitializeComponent();
    }

    public AppBehaviorSettings BehaviorSettings { get; private set; } = new();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Program.SetRedirectedActivationHandler(OnAppInstanceActivated);
        RegisterNotifications();
        BehaviorSettings = LoadBehaviorSettings();

        var parsed = _activationAdapter.Parse(Program.InitialActivation);
        if (parsed.Request is { IsStartupTaskActivation: true })
        {
            ObserveTask(
                StartHiddenResidentAsync(),
                terminateWhenUnreachable: true);
            return;
        }

        Volatile.Write(ref _foregroundActivationRequested, 1);

        if (BehaviorSettings.UsesNotificationAreaIcon)
        {
            var iconResult = EnsureNotificationAreaIcon();
            if (!iconResult.IsVisible)
            {
                ObserveTask(DisableUnavailableNotificationAreaModeAsync(iconResult.Message));
            }
        }

        if (parsed.Request is { } request)
        {
            ObserveTask(
                ActivateRequestAsync(request),
                showFatalWhenUnreachable: true,
                terminateWhenUnreachable: true);
        }
        else
        {
            ObserveTask(
                ShowInitialWindowAsync(parsed.Error),
                showFatalWhenUnreachable: true,
                terminateWhenUnreachable: true);
        }

        ObserveTask(EnsureResidentInitializationAsync(configureJumpList: true));
    }

    private AppBehaviorSettings LoadBehaviorSettings()
    {
        try
        {
            return _behaviorSettingsStore.LoadAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Preferences fail closed. In particular, never start an invisible resident
            // process when Package Pilot cannot prove notification-area availability.
            return new AppBehaviorSettings();
        }
    }

    private async Task StartHiddenResidentAsync()
    {
        if (!BehaviorSettings.HideOnStartupTaskActivation
            || !BehaviorSettings.UsesNotificationAreaIcon)
        {
            try
            {
                await _startupRegistration.SetEnabledAsync(false)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // A stale startup registration must not leave an invisible process alive.
            }
            finally
            {
                if (await ShouldCompleteHiddenStartupShutdownAsync())
                {
                    await ShutdownCoreAsync();
                }
            }

            return;
        }

        var result = EnsureNotificationAreaIcon();
        if (!result.IsVisible)
        {
            try
            {
                await DisableUnavailableNotificationAreaModeAsync(result.Message)
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Tray creation already failed. Always terminate instead of retaining an
                // unreachable login-started process while Windows state is reconciled.
            }
            finally
            {
                if (await ShouldCompleteHiddenStartupShutdownAsync())
                {
                    await ShutdownCoreAsync();
                }
            }

            return;
        }

        await LoadCachedUpdateCountAsync();
        await EnsureResidentInitializationAsync(configureJumpList: false);
    }

    private async Task<bool> ShouldCompleteHiddenStartupShutdownAsync()
    {
        if (Volatile.Read(ref _foregroundActivationRequested) != 0)
        {
            return false;
        }

        if (Volatile.Read(ref _redirectedActivationDispatchCount) != 0)
        {
            return false;
        }

        // Give an ordinary launch already being redirected to this startup instance a brief
        // chance to supersede stale-startup cleanup before single-instance ownership is released.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        return Volatile.Read(ref _foregroundActivationRequested) == 0
            && Volatile.Read(ref _redirectedActivationDispatchCount) == 0;
    }

    private async Task ShowInitialWindowAsync(string? activationError)
    {
        await Task.Yield();
        var window = EnsureWindow();
        if (!window.ShowAndActivate())
        {
            throw new InvalidOperationException("Windows could not show the Package Pilot window.");
        }
        if (!string.IsNullOrWhiteSpace(activationError))
        {
            _shellViewModel?.SetStatusMessage(
                $"External activation was rejected: {activationError}");
        }
    }

    private MainWindow EnsureWindow()
    {
        if (_window is not null)
        {
            return _window;
        }

        var operationDiagnostics = new WindowsOperationDiagnosticsService(Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "operation-diagnostics",
            "installer-logs"),
            ApplicationData.Current.LocalFolder.Path);
        var wingetClient = new WingetClient(operationDiagnostics);
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
        var monitoringState = new WindowsBackgroundUpdateRegistrationService()
            .GetCurrent().State;
        var bootSession = new WindowsBootSessionIdentityProvider().GetCurrent();
        _shellViewModel = new ShellViewModel(
            wingetClient,
            _operationQueue,
            _dispatcherQueue ?? throw new InvalidOperationException("The UI dispatcher is unavailable."),
            updateCoordinator: updateCoordinator,
            updateScanWorker: updateScanWorker,
            installedAppInventory: installedAppInventory,
            sourceManagementService: wingetClient,
            notificationRegistrationSupported: _notificationRegistrationSupported,
            notificationRegistered: _notificationManager is not null,
            backgroundMonitoringState: monitoringState,
            getUpdateMonitoringCadence: ReadMonitoringCadence,
            windowActivityService: _windowActivity,
            lifetimeActivityGate: _lifetimeActivityGate,
            currentBootSessionId: bootSession.Identity,
            bootSessionIdentityError: bootSession.Error);
        _shellViewModel.AvailableUpdates.CollectionChanged += OnAvailableUpdatesChanged;

        _window = new MainWindow(
            _shellViewModel,
            new ElevatedSourceManagementBroker(),
            this,
            _lifetimeActivityGate,
            operationDiagnostics);
        _window.ClosingRequested += OnWindowClosingRequested;
        _window.ActivityChanged += OnWindowActivityChanged;
        _window.Closed += OnWindowClosed;
        UpdateWindowActivity();
        return _window;
    }

    private void OnAppInstanceActivated(AppActivationArguments args)
    {
        var parsed = _activationAdapter.Parse(args);
        if (parsed.Request is not { IsStartupTaskActivation: true })
        {
            // This callback can run off the UI thread. Publish foreground intent before
            // dispatch so hidden-startup cleanup cannot overtake an accepted activation.
            Volatile.Write(ref _foregroundActivationRequested, 1);
        }

        Interlocked.Increment(ref _redirectedActivationDispatchCount);
        bool enqueued = _dispatcherQueue?.TryEnqueue(() =>
        {
            Interlocked.Decrement(ref _redirectedActivationDispatchCount);
            ObserveTask(
                ActivateFromWindowsAsync(parsed),
                terminateWhenUnreachable: true);
        }) == true;
        if (!enqueued)
        {
            Interlocked.Decrement(ref _redirectedActivationDispatchCount);
            Program.ShowFatalApplicationError(
                "Package Pilot received a request while its main instance was shutting down. The request was not opened; start Package Pilot again and retry.");
        }
    }

    private async Task ActivateFromWindowsAsync(ActivationParseResult parsed)
    {
        try
        {
            if (parsed.Request is { IsStartupTaskActivation: true })
            {
                // A login trigger redirected to an already-running instance must never hide
                // or navigate a window the user is actively using.
                return;
            }

            if (parsed.Request is { } request)
            {
                Volatile.Write(ref _foregroundActivationRequested, 1);
                await ActivateRequestAsync(request);
            }
            else if (!string.IsNullOrWhiteSpace(parsed.Error))
            {
                Volatile.Write(ref _foregroundActivationRequested, 1);
                var window = EnsureWindow();
                if (!window.ShowAndActivate())
                {
                    throw new InvalidOperationException("Windows could not show the Package Pilot window.");
                }

                UpdateWindowActivity();
                _shellViewModel?.SetStatusMessage(
                    $"External activation was rejected: {parsed.Error}");
            }
        }
        catch (Exception exception) when (IsRecoverable(exception)
            && _window?.IsVisible == true
            && _shellViewModel is not null)
        {
            _shellViewModel.SetStatusMessage(
                $"Windows activation could not be completed (0x{exception.HResult:X8}).");
        }
    }

    private async Task ActivateRequestAsync(AppActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsStartupTaskActivation || _isExiting)
        {
            return;
        }

        var window = EnsureWindow();
        // Establish real foreground activity before any requested check can reach the
        // notification policy. MainPage still records pre-Loaded navigation synchronously.
        if (!window.ShowAndActivate())
        {
            throw new InvalidOperationException("Windows could not show the Package Pilot window.");
        }
        window.InitializeTaskbarProgress();
        UpdateWindowActivity();
        var activation = window.MainPage.ActivateAsync(request);
        await activation;
        ObserveTask(EnsureResidentInitializationAsync(configureJumpList: true));
    }

    private Task EnsureResidentInitializationAsync(bool configureJumpList)
    {
        _residentInitialization ??= InitializeResidentServicesAsync();
        if (configureJumpList && !_jumpListConfigured)
        {
            _jumpListConfigured = true;
            ObserveTask(ConfigureJumpListAsync());
        }

        return _residentInitialization;
    }

    private async Task InitializeResidentServicesAsync()
    {
        try
        {
            await EnsureBackgroundMonitoringAsync(_residentLifetime.Token);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _shellViewModel?.SetBackgroundMonitoringState(
                BackgroundMonitoringState.Unavailable);
        }
    }

    private async Task ConfigureJumpListAsync()
    {
        try
        {
            await new WindowsJumpListService().ConfigureAsync();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Jump Lists are optional shell integration and must not affect startup.
        }
    }

    private void RegisterNotifications()
    {
        AppNotificationManager? manager = null;
        try
        {
            manager = AppNotificationManager.Default;
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
            if (manager is not null)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
            }

            // Notification registration can be disabled by Windows or missing in a
            // development package. Update discovery remains fully usable in-app.
        }
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            var parsed = _activationAdapter.ParseNotification(args);
            if (parsed.Request is { } request)
            {
                ObserveTask(ActivateRequestAsync(request));
            }
        });
    }

    private NotificationAreaIconResult EnsureNotificationAreaIcon()
    {
        try
        {
            if (_notificationAreaIcon is null)
            {
                _notificationAreaIcon = new WindowsNotificationAreaIconService();
                _notificationAreaIcon.ActionRequested += OnNotificationAreaActionRequested;
                _notificationAreaIcon.AvailabilityChanged += OnNotificationAreaAvailabilityChanged;
                _notificationAreaIcon.MenuOpening += OnNotificationAreaMenuOpening;
                _notificationAreaIcon.ShutdownRequested += OnSystemShutdownRequested;
            }

            return _notificationAreaIcon.Show(CreateNotificationAreaOptions());
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            DisposeNotificationAreaIcon();
            return new NotificationAreaIconResult
            {
                State = NotificationAreaIconState.Failed,
                Message = $"Windows could not create the notification-area icon (0x{exception.HResult:X8})."
            };
        }
    }

    private NotificationAreaIconOptions CreateNotificationAreaOptions() => new()
    {
        ToolTip = "Package Pilot",
        UpdateCount = _lastKnownUpdateCount
    };

    private void OnNotificationAreaActionRequested(
        object? sender,
        NotificationAreaActionRequestedEventArgs args)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            switch (args.Action)
            {
                case NotificationAreaAction.Open:
                    if (_window is null)
                    {
                        ObserveTask(ActivateRequestAsync(new AppActivationRequest()));
                    }
                    else
                    {
                        _window.ShowAndActivate();
                        UpdateWindowActivity();
                    }
                    break;
                case NotificationAreaAction.ReviewUpdates:
                    ObserveTask(ActivateRequestAsync(new AppActivationRequest
                    {
                        Destination = AppDestination.Updates
                    }));
                    break;
                case NotificationAreaAction.CheckNow:
                    ObserveTask(ActivateRequestAsync(new AppActivationRequest
                    {
                        Destination = AppDestination.Updates,
                        CheckForUpdates = true
                    }));
                    break;
                case NotificationAreaAction.OpenSettings:
                    ObserveTask(ActivateRequestAsync(new AppActivationRequest
                    {
                        Destination = AppDestination.Settings
                    }));
                    break;
                case NotificationAreaAction.Exit:
                    ObserveTask(RequestExitAsync());
                    break;
            }
        });
    }

    private void OnSystemShutdownRequested(object? sender, EventArgs args)
    {
        _systemShutdownRequested = true;
        _isExiting = true;
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_window is not null)
            {
                _window.Close();
            }
            else
            {
                ObserveTask(ShutdownCoreAsync(), showFatalWhenUnreachable: false);
            }
        });
    }

    private void OnNotificationAreaAvailabilityChanged(
        object? sender,
        NotificationAreaAvailabilityChangedEventArgs args)
    {
        if (args.Result.IsVisible || _isExiting || _systemShutdownRequested)
        {
            return;
        }

        _dispatcherQueue?.TryEnqueue(() =>
        {
            ObserveTask(HandleNotificationAreaLossAsync(args.Result.Message),
                showFatalWhenUnreachable: false);
        });
    }

    private async Task HandleNotificationAreaLossAsync(string? message)
    {
        bool windowReachable = await TryRestoreWindowAfterNotificationAreaLossAsync();
        await DisableUnavailableNotificationAreaModeAsync(
            message ?? "The notification-area icon is no longer available.");
        if (windowReachable)
        {
            return;
        }

        // Safety wins over process cleanup: if package or source work was already active when
        // Explorer disappeared and the window cannot be restored, let it finish, then exit.
        // The combined commit closes both activity boundaries without a start-versus-exit race.
        while (!CanBeginOrderlyExit(out var blockingDestination))
        {
            if (blockingDestination != AppDestination.Activity)
            {
                await _lifetimeActivityGate.WaitForIdleAsync();
            }
            else if (_operationQueue is not null)
            {
                await _operationQueue.WaitForIdleAsync();
            }
        }

        await ShutdownCoreAsync();
    }

    private async Task<bool> TryRestoreWindowAfterNotificationAreaLossAsync()
    {
        if (_window is null)
        {
            return false;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (_window.ShowAndActivate())
            {
                UpdateWindowActivity();
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return false;
    }

    private void OnWindowClosingRequested(object? sender, CancelEventArgs args)
    {
        if (_isExiting || _systemShutdownRequested)
        {
            return;
        }

        if (Volatile.Read(ref _lifetimeTransitionCount) > 0)
        {
            args.Cancel = true;
            ShowOperationsPreventExit(AppDestination.Settings);
            return;
        }

        if (BehaviorSettings.CloseToNotificationArea
            && _notificationAreaIcon?.IsVisible == true
            && _window is not null)
        {
            args.Cancel = true;
            if (!_window.HideToNotificationArea())
            {
                _window.ShowAndActivate();
                _shellViewModel?.SetStatusMessage(
                    "Windows could not hide Package Pilot safely, so the window remains open.");
            }

            UpdateWindowActivity();
            return;
        }

        if (_lifetimeActivityGate.Snapshot.ActiveKind is { } activeActivity)
        {
            args.Cancel = true;
            ShowOperationsPreventExit(ToBlockingDestination(activeActivity));
            return;
        }

        if (!CanBeginOrderlyExit(out var blockingDestination))
        {
            args.Cancel = true;
            ShowOperationsPreventExit(blockingDestination);
            return;
        }

        if (BehaviorSettings.CloseToNotificationArea)
        {
            // Never cancel the real close unless the user has a confirmed way to reopen.
            ObserveTask(DisableUnavailableNotificationAreaModeAsync(
                "The notification-area icon is no longer available."));
        }
    }

    private void OnWindowActivityChanged(object? sender, EventArgs args) =>
        UpdateWindowActivity();

    private void UpdateWindowActivity()
    {
        _windowActivity.Set(
            _window?.IsVisible == true,
            _window?.IsActive == true);
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

            UpdateUpdateCount(_shellViewModel.AvailableUpdates.Count, updateBadge: true);
        });
    }

    private void UpdateUpdateCount(int count, bool updateBadge)
    {
        _lastKnownUpdateCount = Math.Max(0, count);
        if (updateBadge)
        {
            WindowsUpdateNotificationService.SetBadgeCount(_lastKnownUpdateCount);
        }

        if (_notificationAreaIcon?.IsVisible == true)
        {
            try
            {
                _ = _notificationAreaIcon.Update(CreateNotificationAreaOptions());
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Explorer can restart between the visibility check and update. The tray
                // service will restore the last options on TaskbarCreated.
            }
        }
    }

    private async Task LoadCachedUpdateCountAsync()
    {
        try
        {
            var store = new JsonUpdateSnapshotStore(Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "update-snapshot.json"));
            var snapshot = await store.LoadAsync();
            UpdateUpdateCount(snapshot?.Updates.Count ?? 0, updateBadge: false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // The background worker owns badge state. A corrupt cache must not prevent the
            // resident icon from opening the app for a manual check.
        }
    }

    private void OnNotificationAreaMenuOpening(object? sender, EventArgs args)
    {
        try
        {
            // The background host atomically replaces this small file. Read only on an
            // explicit menu open so the resident process performs no polling or idle I/O.
            var store = new JsonUpdateSnapshotStore(Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "update-snapshot.json"));
            var snapshot = store.LoadAsync()
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            UpdateUpdateCount(snapshot?.Updates.Count ?? 0, updateBadge: false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Keep the last known count if the cache is being replaced or is unavailable.
        }
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

    private async Task EnsureBackgroundMonitoringAsync(CancellationToken cancellationToken)
    {
        var cadence = ReadMonitoringCadence();
        var registration = new WindowsBackgroundUpdateRegistrationService();
        var current = registration.GetCurrent();
        BackgroundMonitoringResult result = current;
        if ((cadence == UpdateMonitoringCadence.Manual
                && current.State != BackgroundMonitoringState.Disabled)
            || (cadence != UpdateMonitoringCadence.Manual
                && (current.State != BackgroundMonitoringState.Registered
                    || current.Cadence != cadence)))
        {
            result = await registration.ConfigureAsync(cadence, cancellationToken);
        }

        _shellViewModel?.SetBackgroundMonitoringState(result.State);
    }

    public async Task<NotificationAreaIconResult> SetNotificationAreaEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _lifetimeTransitionCount);
        IDisposable? activity = null;
        try
        {
            activity = _lifetimeActivityGate.TryEnter(
                AppLifetimeActivityKind.WindowsIntegration);
            if (activity is null)
            {
                return new NotificationAreaIconResult
                {
                    State = NotificationAreaIconState.Failed,
                    Message = "Finish the current package-source or Windows integration operation before changing notification-area mode."
                };
            }

            await _lifetimeTransitionGate.WaitAsync(cancellationToken);
            try
            {
                if (enabled)
                {
                    var result = EnsureNotificationAreaIcon();
                    if (!result.IsVisible)
                    {
                        return result;
                    }

                    var proposed = new AppBehaviorSettings
                    {
                        ShowNotificationAreaIcon = true,
                        HideOnStartupTaskActivation = true,
                        MinimizeToNotificationArea = false,
                        CloseToNotificationArea = true
                    };
                    try
                    {
                        await _behaviorSettingsStore.SaveAsync(proposed, cancellationToken);
                        BehaviorSettings = proposed;
                        return result;
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        DisposeNotificationAreaIcon();
                        return new NotificationAreaIconResult
                        {
                            State = NotificationAreaIconState.Failed,
                            Message = $"Package Pilot could not save the notification-area preference (0x{exception.HResult:X8})."
                        };
                    }
                }

                var startup = await _startupRegistration.GetStateAsync(cancellationToken);
                if (startup.IsEnabled)
                {
                    startup = await _startupRegistration.SetEnabledAsync(false, cancellationToken);
                    if (startup.IsEnabled)
                    {
                        return new NotificationAreaIconResult
                        {
                            State = NotificationAreaIconState.Failed,
                            Message = startup.Message
                                ?? "Windows requires Start with Windows to remain enabled."
                        };
                    }
                }

                var disabled = new AppBehaviorSettings();
                try
                {
                    if (_window is { IsVisible: false } hiddenWindow)
                    {
                        if (!hiddenWindow.ShowAndActivate())
                        {
                            return new NotificationAreaIconResult
                            {
                                State = NotificationAreaIconState.Failed,
                                Message = "Windows could not restore the Package Pilot window, so notification-area mode remains enabled."
                            };
                        }

                        UpdateWindowActivity();
                    }

                    await _behaviorSettingsStore.SaveAsync(disabled, cancellationToken);
                    BehaviorSettings = disabled;

                    DisposeNotificationAreaIcon();
                    return new NotificationAreaIconResult
                    {
                        State = NotificationAreaIconState.Hidden
                    };
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    return new NotificationAreaIconResult
                    {
                        State = NotificationAreaIconState.Failed,
                        Message = $"Package Pilot could not disable notification-area mode (0x{exception.HResult:X8})."
                    };
                }
            }
            finally
            {
                _lifetimeTransitionGate.Release();
            }
        }
        finally
        {
            activity?.Dispose();
            Interlocked.Decrement(ref _lifetimeTransitionCount);
        }
    }

    public async Task<StartupRegistrationResult> GetStartupRegistrationAsync(
        CancellationToken cancellationToken = default)
    {
        await _lifetimeTransitionGate.WaitAsync(cancellationToken);
        try
        {
            return await _startupRegistration.GetStateAsync(cancellationToken);
        }
        finally
        {
            _lifetimeTransitionGate.Release();
        }
    }

    public async Task<StartupRegistrationResult> SetStartupEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _lifetimeTransitionCount);
        IDisposable? activity = null;
        try
        {
            activity = _lifetimeActivityGate.TryEnter(
                AppLifetimeActivityKind.WindowsIntegration);
            if (activity is null)
            {
                return new StartupRegistrationResult
                {
                    State = StartupRegistrationState.Failed,
                    Message = "Finish the current package-source or Windows integration operation before changing Windows startup."
                };
            }

            await _lifetimeTransitionGate.WaitAsync(cancellationToken);
            try
            {
                if (enabled
                    && (!BehaviorSettings.UsesNotificationAreaIcon
                        || _notificationAreaIcon?.IsVisible != true))
                {
                    return new StartupRegistrationResult
                    {
                        State = StartupRegistrationState.Failed,
                        Message = "Enable notification-area mode before starting Package Pilot with Windows."
                    };
                }

                return await _startupRegistration.SetEnabledAsync(enabled, cancellationToken);
            }
            finally
            {
                _lifetimeTransitionGate.Release();
            }
        }
        finally
        {
            activity?.Dispose();
            Interlocked.Decrement(ref _lifetimeTransitionCount);
        }
    }

    public async Task OpenWindowsStartupSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await global::Windows.System.Launcher.LaunchUriAsync(
            new Uri("ms-settings:startupapps"));
    }

    private async Task DisableUnavailableNotificationAreaModeAsync(string? message)
    {
        var disabled = new AppBehaviorSettings();
        BehaviorSettings = disabled;
        try
        {
            await _behaviorSettingsStore.SaveAsync(disabled);
            await _startupRegistration.SetEnabledAsync(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // The runtime still refuses to hide without a visible icon. A later Settings
            // visit can reconcile any Windows-owned startup state.
        }

        DisposeNotificationAreaIcon();
        if (!string.IsNullOrWhiteSpace(message))
        {
            _shellViewModel?.SetStatusMessage(message);
        }
    }

    private async Task RequestExitAsync()
    {
        if (Volatile.Read(ref _lifetimeTransitionCount) > 0)
        {
            ShowOperationsPreventExit(AppDestination.Settings);
            return;
        }

        if (!CanBeginOrderlyExit(out var blockingDestination))
        {
            ShowOperationsPreventExit(blockingDestination);
            return;
        }

        _isExiting = true;
        if (_window is not null)
        {
            _window.Close();
            return;
        }

        await ShutdownCoreAsync();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.ClosingRequested -= OnWindowClosingRequested;
            _window.ActivityChanged -= OnWindowActivityChanged;
            _window.Closed -= OnWindowClosed;
        }

        UpdateWindowActivity();
        ObserveTask(ShutdownCoreAsync(), showFatalWhenUnreachable: false);
    }

    private Task ShutdownCoreAsync() => _shutdown ??= ShutdownCoreImplementationAsync();

    private async Task ShutdownCoreImplementationAsync()
    {
        _isExiting = true;
        try
        {
            _residentLifetime.Cancel();
            if (_residentInitialization is not null)
            {
                try
                {
                    await _residentInitialization.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                }
                catch (TimeoutException)
                {
                    // Registration is transactional and receives the lifetime token. The hard
                    // bound prevents shell shutdown from waiting indefinitely on Windows policy.
                }
            }

            if (_shellViewModel is not null)
            {
                _shellViewModel.AvailableUpdates.CollectionChanged -= OnAvailableUpdatesChanged;
            }

            if (_notificationManager is not null)
            {
                _notificationManager.NotificationInvoked -= OnNotificationInvoked;
                try
                {
                    _notificationManager.Unregister();
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    // Windows may already be tearing down notification infrastructure.
                }

                _notificationManager = null;
            }

            _shellViewModel?.Dispose();
            if (_operationQueue is not null)
            {
                await _operationQueue.DisposeAsync();
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Shutdown is best effort. The finally block must always release native shell
            // resources, single-instance ownership, and the WinUI message loop.
        }
        finally
        {
            DisposeNotificationAreaIcon();
            _shellViewModel = null;
            _operationQueue = null;
            _window = null;
            _updateNotificationService = null;
            _dispatcherQueue = null;
            _residentLifetime.Dispose();
            Program.BeginShutdown();
            Exit();
        }
    }

    private bool CanBeginOrderlyExit(out AppDestination blockingDestination)
    {
        bool canExit = _lifetimeActivityGate.TryBeginShutdownIfIdle(
            () => _operationQueue?.TryBeginShutdownIfIdle() ?? true,
            out var blockingActivity);
        blockingDestination = blockingActivity is { } activity
            ? ToBlockingDestination(activity)
            : AppDestination.Activity;
        return canExit;
    }

    private static AppDestination ToBlockingDestination(
        AppLifetimeActivityKind activity) => activity switch
        {
            AppLifetimeActivityKind.SourceRefresh or
            AppLifetimeActivityKind.SourceMutation => AppDestination.Sources,
            _ => AppDestination.Settings
        };

    private void ShowOperationsPreventExit(AppDestination destination)
    {
        _shellViewModel?.SetStatusMessage(destination switch
        {
            AppDestination.Sources =>
                "A package-source operation is still running. Finish it from Sources before exiting.",
            AppDestination.Settings =>
                "A Windows integration setting is still being applied. Finish it in Settings before exiting.",
            _ =>
                "Package changes are still queued or running. Finish or cancel them from Activity before exiting."
        });
        ObserveTask(ActivateRequestAsync(new AppActivationRequest
        {
            Destination = destination
        }));
    }

    private void ObserveTask(
        Task task,
        bool showFatalWhenUnreachable = false,
        bool terminateWhenUnreachable = false) =>
        _ = ObserveTaskCoreAsync(
            task,
            showFatalWhenUnreachable,
            terminateWhenUnreachable);

    private async Task ObserveTaskCoreAsync(
        Task task,
        bool showFatalWhenUnreachable,
        bool terminateWhenUnreachable)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (_isExiting || _systemShutdownRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            bool hasReopenAffordance = _window?.IsVisible == true
                || _notificationAreaIcon?.IsVisible == true;
            if (hasReopenAffordance)
            {
                _shellViewModel?.SetStatusMessage(
                    $"Package Pilot could not complete the request (0x{exception.HResult:X8}).");
                return;
            }

            if (showFatalWhenUnreachable)
            {
                Program.ShowFatalApplicationError(
                    $"Package Pilot could not start safely (0x{exception.HResult:X8}). The process will close.");
            }

            if (terminateWhenUnreachable
                && CanBeginOrderlyExit(out _))
            {
                ForceExitAfterUnhandledFailure();
            }
        }
    }

    private void ForceExitAfterUnhandledFailure()
    {
        _isExiting = true;
        try
        {
            DisposeNotificationAreaIcon();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }

        Program.BeginShutdown();
        Exit();
    }

    private void DisposeNotificationAreaIcon()
    {
        if (_notificationAreaIcon is null)
        {
            return;
        }

        _notificationAreaIcon.ActionRequested -= OnNotificationAreaActionRequested;
        _notificationAreaIcon.AvailabilityChanged -= OnNotificationAreaAvailabilityChanged;
        _notificationAreaIcon.MenuOpening -= OnNotificationAreaMenuOpening;
        _notificationAreaIcon.ShutdownRequested -= OnSystemShutdownRequested;
        try
        {
            _notificationAreaIcon.Dispose();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Explorer integration must not prevent a normal app shutdown.
        }

        _notificationAreaIcon = null;
    }

    private static bool IsRecoverable(Exception exception) => exception is not
        OperationCanceledException and not
        OutOfMemoryException and not
        StackOverflowException and not
        AccessViolationException;

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
