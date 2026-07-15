using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using Windows.Storage;

namespace PackagePilot.App.ViewModels;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IWingetClient _wingetClient;
    private readonly IOperationQueue _operationQueue;
    private readonly IUpdateCoordinator _updateCoordinator;
    private readonly UpdateScanWorker _updateScanWorker;
    private readonly IInstalledAppInventory? _installedAppInventory;
    private readonly ISourceManagementService? _sourceManagementService;
    private readonly IAppLifetimeActivityGate _lifetimeActivityGate;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<UpdateMonitoringCadence> _getUpdateMonitoringCadence;
    private readonly IWindowActivityService? _windowActivityService;
    private readonly HashSet<Guid> _observedCompletions = [];
    private readonly Dictionary<string, string> _acceptedSourceAgreementFingerprints =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _searchCancellation;
    private UpdateSnapshot? _updateSnapshot;
    private WingetCapabilities _capabilities = WingetCapabilities.Unavailable("Checking Windows Package Manager…");
    private PackageSummary? _selectedPackage;
    private PackageDetails? _selectedDetails;
    private OperationQueueEntry? _currentOperation;
    private string _searchText = string.Empty;
    private string _statusMessage = "Preparing Package Pilot…";
    private bool _isBusy;
    private bool _isSearchTruncated;
    private bool _isDetailsLoading;
    private bool _hasAcceptedSourceAgreements;
    private UpdateCheckState _updatesCheckState = UpdateCheckState.NotChecked;
    private DateTimeOffset? _lastUpdateCheckAt;
    private string? _updateCheckError;
    private SourceManagementCapabilities _sourceManagementCapabilities = new();
    private WindowsIntegrationCapabilities _windowsIntegrationCapabilities = new();
    private bool _isManagingSources;
    private bool _disposed;

    public ShellViewModel(
        IWingetClient wingetClient,
        IOperationQueue operationQueue,
        DispatcherQueue dispatcher,
        IUpdateCoordinator? updateCoordinator = null,
        UpdateScanWorker? updateScanWorker = null,
        IInstalledAppInventory? installedAppInventory = null,
        ISourceManagementService? sourceManagementService = null,
        bool notificationRegistrationSupported = false,
        bool notificationRegistered = false,
        BackgroundMonitoringState backgroundMonitoringState = BackgroundMonitoringState.Disabled,
        Func<UpdateMonitoringCadence>? getUpdateMonitoringCadence = null,
        IWindowActivityService? windowActivityService = null,
        IAppLifetimeActivityGate? lifetimeActivityGate = null)
    {
        _wingetClient = wingetClient;
        _operationQueue = operationQueue;
        _dispatcher = dispatcher;
        _installedAppInventory = installedAppInventory;
        _sourceManagementService = sourceManagementService;
        _lifetimeActivityGate = lifetimeActivityGate ?? new AppLifetimeActivityGate();
        _getUpdateMonitoringCadence = getUpdateMonitoringCadence
            ?? (() => UpdateMonitoringCadence.Daily);
        _windowActivityService = windowActivityService;
        _windowsIntegrationCapabilities = new WindowsIntegrationCapabilities
        {
            NotificationRegistrationSupported = notificationRegistrationSupported,
            NotificationRegistered = notificationRegistered,
            BackgroundMonitoringState = backgroundMonitoringState
        };
        LoadSourceAgreementConsents();
        _updateCoordinator = updateCoordinator ?? new UpdateCoordinator(
            wingetClient,
            new JsonUpdateSnapshotStore(Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "update-snapshot.json")));
        _updateScanWorker = updateScanWorker ?? new UpdateScanWorker(
            _updateCoordinator,
            new UpdateNotificationPolicy(),
            new NullUpdateNotificationSink());
        _operationQueue.Changed += OnOperationQueueChanged;

        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAllAsync, () => IsReady && !IsBusy);
        SelectPackageCommand = new AsyncRelayCommand<PackageSummary?>(SelectPackageAsync);
    }

    public ObservableCollection<PackageSummary> SearchResults { get; } = [];
    public ObservableCollection<PackageSummary> InstalledPackages { get; } = [];
    public ObservableCollection<InstalledApp> InstalledApps { get; } = [];
    public ObservableCollection<InstalledAppProviderStatus> InstalledAppProviders { get; } = [];
    public ObservableCollection<PackageSourceInfo> ManagedSources { get; } = [];
    public ObservableCollection<PackageSummary> AvailableUpdates { get; } = [];
    public ObservableCollection<PackageSourceStatus> SourceStatuses { get; } = [];
    public ObservableCollection<OperationQueueEntry> PendingOperations { get; } = [];
    public ObservableCollection<OperationResult> OperationHistory { get; } = [];

    public IAsyncRelayCommand InitializeCommand { get; }
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<PackageSummary?> SelectPackageCommand { get; }

    public WingetCapabilities Capabilities
    {
        get => _capabilities;
        private set
        {
            if (SetProperty(ref _capabilities, value))
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(HealthTitle));
                OnPropertyChanged(nameof(HealthMessage));
                UpdateWindowsIntegrationCapabilities();
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsReady => Capabilities.MeetsMinimumContract;

    public string HealthTitle => IsReady ? "Windows Package Manager is ready" : "WinGet needs attention";

    public string HealthMessage => IsReady
        ? $"Connected to WinGet {Capabilities.Version ?? $"contract {Capabilities.ContractVersion}"}."
        : Capabilities.UnavailableReason ?? "Install or update App Installer to continue.";

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsSearchTruncated
    {
        get => _isSearchTruncated;
        private set => SetProperty(ref _isSearchTruncated, value);
    }

    public bool IsDetailsLoading
    {
        get => _isDetailsLoading;
        private set => SetProperty(ref _isDetailsLoading, value);
    }

    public UpdateCheckState UpdatesCheckState
    {
        get => _updatesCheckState;
        private set
        {
            if (SetProperty(ref _updatesCheckState, value))
            {
                OnPropertyChanged(nameof(IsCheckingForUpdates));
            }
        }
    }

    public bool IsCheckingForUpdates => UpdatesCheckState == UpdateCheckState.Checking;

    public DateTimeOffset? LastUpdateCheckAt
    {
        get => _lastUpdateCheckAt;
        private set => SetProperty(ref _lastUpdateCheckAt, value);
    }

    public string? UpdateCheckError
    {
        get => _updateCheckError;
        private set => SetProperty(ref _updateCheckError, value);
    }

    public SourceManagementCapabilities SourceManagementCapabilities
    {
        get => _sourceManagementCapabilities;
        private set
        {
            if (SetProperty(ref _sourceManagementCapabilities, value))
            {
                UpdateWindowsIntegrationCapabilities();
            }
        }
    }

    public WindowsIntegrationCapabilities WindowsIntegrationCapabilities
    {
        get => _windowsIntegrationCapabilities;
        private set
        {
            if (SetProperty(ref _windowsIntegrationCapabilities, value))
            {
                OnPropertyChanged(nameof(WindowsCapabilitySummary));
            }
        }
    }

    public string WindowsCapabilitySummary
    {
        get
        {
            var capabilities = WindowsIntegrationCapabilities;
            var repair = capabilities.SupportsPackageRepair ? "repair available" : "repair unavailable";
            var sources = capabilities.SupportsSourceMutation
                ? capabilities.SupportsSourceExplicitEdit
                    ? "source administration and explicit editing available"
                    : "source administration available"
                : capabilities.SupportsSourceRefresh
                    ? "source refresh available"
                    : "source administration unavailable";
            var background = capabilities.BackgroundMonitoringState switch
            {
                BackgroundMonitoringState.Registered => "background monitoring registered",
                BackgroundMonitoringState.Disabled => "background monitoring set to manual",
                BackgroundMonitoringState.Denied => "background monitoring denied by Windows",
                _ => "background monitoring unavailable"
            };
            var notifications = capabilities.NotificationRegistered
                ? "notifications registered"
                : capabilities.NotificationRegistrationSupported
                    ? "notifications not registered"
                    : "notifications unavailable";
            return $"{HealthMessage} Package {repair}; {sources}; {background}; {notifications}.";
        }
    }

    public bool IsManagingSources
    {
        get => _isManagingSources;
        private set => SetProperty(ref _isManagingSources, value);
    }

    public PackageSummary? SelectedPackage
    {
        get => _selectedPackage;
        private set => SetProperty(ref _selectedPackage, value);
    }

    public PackageDetails? SelectedDetails
    {
        get => _selectedDetails;
        private set
        {
            if (SetProperty(ref _selectedDetails, value))
            {
                OnPropertyChanged(nameof(SelectedPackageHasAgreements));
            }
        }
    }

    public bool SelectedPackageHasAgreements => SelectedDetails?.Agreements.Count > 0;

    public OperationQueueEntry? CurrentOperation
    {
        get => _currentOperation;
        private set
        {
            if (SetProperty(ref _currentOperation, value))
            {
                OnPropertyChanged(nameof(HasActiveOperation));
                OnPropertyChanged(nameof(ActivitySummary));
            }
        }
    }

    public bool HasActiveOperation => CurrentOperation is not null || PendingOperations.Count > 0;

    public string ActivitySummary => CurrentOperation is { } current
        ? $"{FormatOperationKind(current.Operation.Kind)} {current.Operation.DisplayName} — {FormatState(current.Progress.State)}"
        : PendingOperations.Count > 0
            ? $"{PendingOperations.Count} operation{(PendingOperations.Count == 1 ? string.Empty : "s")} queued"
            : "No active operations";

    public async Task InitializeAsync()
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Checking App Installer and WinGet…";

        var scheduleAutomaticUpdateCheck = false;
        try
        {
            _updateSnapshot = await _updateCoordinator.LoadAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
            ApplyUpdateSnapshot(
                _updateSnapshot,
                _updateCoordinator.GetState(
                    _updateSnapshot,
                    _getUpdateMonitoringCadence()));

            await _operationQueue.Initialization.ConfigureAwait(true);
            ApplyQueueSnapshot(_operationQueue.Snapshot);

            Capabilities = await _wingetClient.GetCapabilitiesAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
            if (!IsReady)
            {
                StatusMessage = Capabilities.UnavailableReason ?? "WinGet is unavailable.";
                return;
            }

            var installedTask = LoadInstalledAppsAsync(_lifetimeCancellation.Token);
            var sourcesTask = LoadSourceStatusesAsync();
            await Task.WhenAll(installedTask, sourcesTask).ConfigureAwait(true);

            ApplyInstalledApps(await installedTask.ConfigureAwait(true));
            if (await sourcesTask.ConfigureAwait(true) is { } sources)
            {
                ReplaceAll(SourceStatuses, sources);
            }
            var cadence = _getUpdateMonitoringCadence();
            scheduleAutomaticUpdateCheck = cadence != UpdateMonitoringCadence.Manual
                && _updateCoordinator.ShouldAutomaticallyCheck(_updateSnapshot, cadence);
            StatusMessage = "Ready";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Capabilities = WingetCapabilities.Unavailable(ex.Message);
            StatusMessage = "Package Pilot could not initialize WinGet.";
        }
        finally
        {
            IsBusy = false;
        }

        if (scheduleAutomaticUpdateCheck && !_disposed)
        {
            _ = RefreshUpdatesCoreAsync(UpdateCheckReason.Automatic);
        }
    }

    public bool HasAcceptedSourceAgreements
    {
        get => _hasAcceptedSourceAgreements;
        private set => SetProperty(ref _hasAcceptedSourceAgreements, value);
    }

    public Task SearchAsync() => SearchCoreAsync();

    public Task SearchAcceptingSourceAgreementsAsync(
        IEnumerable<PackageSourceStatus> acceptedSources)
    {
        ArgumentNullException.ThrowIfNull(acceptedSources);
        foreach (var source in acceptedSources)
        {
            if (string.IsNullOrWhiteSpace(source.Id)
                || string.IsNullOrWhiteSpace(source.AgreementFingerprint))
            {
                continue;
            }

            _acceptedSourceAgreementFingerprints[source.Id] = source.AgreementFingerprint;
            ApplicationData.Current.LocalSettings.Values[
                $"sourceAgreementConsent:{source.Id}"] = source.AgreementFingerprint;
        }

        HasAcceptedSourceAgreements = _acceptedSourceAgreementFingerprints.Count > 0;
        return SearchCoreAsync();
    }

    private async Task SearchCoreAsync()
    {
        if (!IsReady)
        {
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var cancellationToken = _searchCancellation.Token;
        var searchText = SearchText.Trim();

        if (searchText.Length < 2)
        {
            SearchResults.Clear();
            IsSearchTruncated = false;
            StatusMessage = searchText.Length == 0 ? "Search WinGet to discover packages." : "Type at least two characters.";
            return;
        }

        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(true);
            IsBusy = true;
            StatusMessage = $"Searching for “{searchText}”…";

            var result = await _wingetClient.SearchAsync(
                new PackageQuery
                {
                    SearchText = searchText,
                    Limit = 100,
                    AcceptedSourceAgreementFingerprints =
                        new Dictionary<string, string>(_acceptedSourceAgreementFingerprints)
                },
                cancellationToken).ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();

            ReplaceAll(SearchResults, result.Packages);
            ReplaceAll(SourceStatuses, result.Sources);
            IsSearchTruncated = result.IsTruncated;
            StatusMessage = result.Packages.Count == 0
                ? "No packages matched this search."
                : $"Found {result.Packages.Count} package{(result.Packages.Count == 1 ? string.Empty : "s")}.";
        }
        catch (OperationCanceledException)
        {
            // A newer search owns the UI state.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    public async Task RefreshAllAsync()
    {
        if (!IsReady || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Refreshing installed packages and updates…";
        try
        {
            var installedTask = LoadInstalledAppsAsync(_lifetimeCancellation.Token);
            var sourcesTask = LoadSourceStatusesAsync();
            var updatesTask = RefreshUpdatesCoreAsync(UpdateCheckReason.Manual);
            await Task.WhenAll(installedTask, sourcesTask).ConfigureAwait(true);
            ApplyInstalledApps(await installedTask.ConfigureAwait(true));
            if (await sourcesTask.ConfigureAwait(true) is { } sources)
            {
                ReplaceAll(SourceStatuses, sources);
            }
            await updatesTask.ConfigureAwait(true);
            StatusMessage = "Package information is up to date.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task RefreshDiscoverAsync() => SearchAsync();

    public async Task RefreshInstalledAsync()
    {
        if (!IsReady || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Refreshing installed packages…";
        try
        {
            ApplyInstalledApps(await LoadInstalledAppsAsync(_lifetimeCancellation.Token).ConfigureAwait(true));
            StatusMessage = "Installed packages are up to date.";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"Installed package refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task RefreshUpdatesAsync() => RefreshUpdatesCoreAsync(UpdateCheckReason.Manual);

    public async Task RefreshSourcesAsync()
    {
        if (!IsReady || IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Refreshing package sources…";
        try
        {
            var sources = await LoadSourceStatusesAsync().ConfigureAwait(true);
            if (sources is null)
            {
                SetSourceOperationBusyStatus();
                return;
            }

            ReplaceAll(SourceStatuses, sources);
            StatusMessage = "Package sources are up to date.";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"Source refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshManagedSourcesAsync()
    {
        if (_sourceManagementService is null || IsManagingSources)
        {
            return;
        }

        using var activity = _lifetimeActivityGate.TryEnter(
            AppLifetimeActivityKind.SourceRefresh);
        if (activity is null)
        {
            SetSourceOperationBusyStatus();
            return;
        }

        IsManagingSources = true;
        try
        {
            var capabilitiesTask = _sourceManagementService.GetSourceManagementCapabilitiesAsync(
                _lifetimeCancellation.Token);
            var sourcesTask = _sourceManagementService.GetSourceDetailsAsync(
                _lifetimeCancellation.Token);
            await Task.WhenAll(capabilitiesTask, sourcesTask).ConfigureAwait(true);
            SourceManagementCapabilities = await capabilitiesTask.ConfigureAwait(true);
            ReplaceAll(ManagedSources, await sourcesTask.ConfigureAwait(true));
            StatusMessage = "Package sources are up to date.";
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"Source refresh failed: {ex.Message}";
        }
        finally
        {
            activity.Dispose();
            IsManagingSources = false;
        }
    }

    public async Task<SourceOperationResult?> RefreshManagedSourceAsync(string sourceName)
    {
        if (_sourceManagementService is null || IsManagingSources)
        {
            return null;
        }

        using var activity = _lifetimeActivityGate.TryEnter(
            AppLifetimeActivityKind.SourceRefresh);
        if (activity is null)
        {
            SetSourceOperationBusyStatus();
            return null;
        }

        IsManagingSources = true;
        try
        {
            var result = await _sourceManagementService.RefreshSourceAsync(
                sourceName,
                cancellationToken: _lifetimeCancellation.Token).ConfigureAwait(true);
            StatusMessage = result.Message;
            return result;
        }
        finally
        {
            activity.Dispose();
            IsManagingSources = false;
        }
    }

    public Task SelectPackageAsync(PackageSummary? package) =>
        SelectPackageCoreAsync(
            package,
            package is not null && IsSourceAgreementAccepted(package.Key.SourceId));

    private async Task<IReadOnlyList<PackageSourceStatus>?> LoadSourceStatusesAsync()
    {
        using var activity = _lifetimeActivityGate.TryEnter(
            AppLifetimeActivityKind.SourceRefresh);
        if (activity is null)
        {
            return null;
        }

        return await _wingetClient.GetSourcesAsync(_lifetimeCancellation.Token)
            .ConfigureAwait(true);
    }

    private void SetSourceOperationBusyStatus()
    {
        if (!_lifetimeCancellation.IsCancellationRequested)
        {
            StatusMessage = _lifetimeActivityGate.Snapshot.IsShutdownCommitted
                ? "Package Pilot is closing; no new source work was started."
                : "Another package-source operation is already running.";
        }
    }

    public Task SelectPackageAcceptingSourceAgreementsAsync(PackageSummary package) =>
        SelectPackageCoreAsync(package, acceptSourceAgreements: true);

    public void AcceptPackageSourceAgreement(
        PackageSummary package,
        IReadOnlyList<PackageAgreement> agreements)
    {
        var snapshot = SourceAgreementSnapshot.Create(package.Key.SourceId, agreements);
        _acceptedSourceAgreementFingerprints[package.Key.SourceId] = snapshot.Fingerprint;
        ApplicationData.Current.LocalSettings.Values[
            $"sourceAgreementConsent:{package.Key.SourceId}"] = snapshot.Fingerprint;
        HasAcceptedSourceAgreements = true;
    }

    public bool IsSourceAgreementAccepted(string sourceId)
    {
        if (!_acceptedSourceAgreementFingerprints.TryGetValue(sourceId, out var accepted))
        {
            return false;
        }

        var current = SourceStatuses.FirstOrDefault(source => string.Equals(
            source.Id,
            sourceId,
            StringComparison.OrdinalIgnoreCase));
        return current is not null
            && !string.IsNullOrWhiteSpace(current.AgreementFingerprint)
            && string.Equals(
                accepted,
                current.AgreementFingerprint,
                StringComparison.Ordinal);
    }

    private async Task SelectPackageCoreAsync(
        PackageSummary? package,
        bool acceptSourceAgreements)
    {
        SelectedPackage = package;
        SelectedDetails = null;
        if (package is null || !IsReady)
        {
            return;
        }

        IsDetailsLoading = true;
        try
        {
            SelectedDetails = await _wingetClient.GetPackageDetailsAsync(
                package.Key,
                new InstallPreferences
                {
                    AcceptSourceAgreements = acceptSourceAgreements,
                    AcceptedSourceAgreementFingerprint =
                        _acceptedSourceAgreementFingerprints.GetValueOrDefault(package.Key.SourceId)
                }).ConfigureAwait(true);
            if (acceptSourceAgreements)
            {
                HasAcceptedSourceAgreements = true;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load package details: {ex.Message}";
        }
        finally
        {
            IsDetailsLoading = false;
        }
    }

    public Guid EnqueueOperation(
        PackageSummary package,
        PackageOperationKind kind,
        bool acceptedSourceAgreements = false,
        bool acceptedPackageAgreements = false,
        InstallerScope scope = InstallerScope.Unknown,
        PackageArchitecture architecture = PackageArchitecture.Unknown)
    {
        var preferences = new InstallPreferences
        {
            AcceptSourceAgreements = acceptedSourceAgreements,
            AcceptedSourceAgreementFingerprint =
                _acceptedSourceAgreementFingerprints.GetValueOrDefault(package.Key.SourceId),
            AcceptPackageAgreements = acceptedPackageAgreements,
            Scope = scope,
            Architecture = architecture
        };

        return _operationQueue.Enqueue(PackageOperation.Create(kind, package.Key, package.Name, preferences));
    }

    public IReadOnlyList<Guid> EnqueueUpdates(IEnumerable<PackageSummary> packages)
    {
        return packages
            .Select(package => EnqueueOperation(package, PackageOperationKind.Upgrade))
            .ToArray();
    }

    public bool TryCancelOperation(Guid operationId) => _operationQueue.TryCancel(operationId);

    public Guid EnqueueMsixRemoval(InstalledApp app, string packageFullName)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        var installation = app.Installations.FirstOrDefault(item =>
            string.Equals(item.PackageFullName, packageFullName, StringComparison.OrdinalIgnoreCase));
        var familyName = installation?.Aliases
            .FirstOrDefault(alias => alias.Kind == InstalledAppAliasKind.PackageFamilyName)?.Value
            ?? string.Empty;
        return _operationQueue.Enqueue(new PackageOperation
        {
            Kind = PackageOperationKind.Uninstall,
            DisplayName = app.Name,
            Target = new MsixTarget
            {
                PackageFullName = packageFullName,
                PackageFamilyName = familyName
            }
        });
    }

    public void ClearHistory() => _operationQueue.ClearHistory();

    public void SetStatusMessage(string message) => StatusMessage = message;

    public void SetBackgroundMonitoringState(BackgroundMonitoringState state)
    {
        WindowsIntegrationCapabilities = WindowsIntegrationCapabilities with
        {
            BackgroundMonitoringState = state
        };
    }

    private void UpdateWindowsIntegrationCapabilities()
    {
        WindowsIntegrationCapabilities = WindowsIntegrationCapabilities with
        {
            SupportsPackageRepair = Capabilities.SupportsPackageRepair,
            SupportsSourceRefresh = SourceManagementCapabilities.SupportsRefresh,
            SupportsSourceMutation = SourceManagementCapabilities.SupportsAdd
                && SourceManagementCapabilities.SupportsRemove
                && SourceManagementCapabilities.SupportsResetOne,
            SupportsSourceExplicitEdit = SourceManagementCapabilities.SupportsExplicitEdit
        };
    }

    private async Task RefreshUpdatesCoreAsync(UpdateCheckReason reason)
    {
        if (!IsReady || _disposed)
        {
            return;
        }

        UpdatesCheckState = UpdateCheckState.Checking;
        UpdateCheckError = null;
        try
        {
            var execution = _windowActivityService is null
                ? await _updateScanWorker.RunAsync(
                    reason,
                    isForegroundWindowActive: true,
                    SourceStatuses.ToArray(),
                    _lifetimeCancellation.Token,
                    _getUpdateMonitoringCadence()).ConfigureAwait(true)
                : await _updateScanWorker.RunAsync(
                    reason,
                    _windowActivityService,
                    SourceStatuses.ToArray(),
                    _lifetimeCancellation.Token,
                    _getUpdateMonitoringCadence()).ConfigureAwait(true);
            var result = execution.Check;
            _updateSnapshot = result.Snapshot;
            ApplyUpdateSnapshot(result.Snapshot, result.State);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            UpdatesCheckState = UpdateCheckState.Failed;
            UpdateCheckError = ex.Message;
        }
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

    private void OnOperationQueueChanged(object? sender, OperationQueueChangedEventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ApplyQueueSnapshot(e.Snapshot);

            var newSuccess = e.Snapshot.History.FirstOrDefault(result =>
                result.IsSuccess && _observedCompletions.Add(result.OperationId));

            if (newSuccess is not null && IsReady)
            {
                _ = RefreshAfterMutationAsync();
            }
        });
    }

    private async Task RefreshAfterMutationAsync()
    {
        Exception? installedRefreshError = null;
        var installedTask = LoadInstalledAppsAsync(_lifetimeCancellation.Token);
        var updatesTask = RefreshUpdatesCoreAsync(UpdateCheckReason.PackageMutation);

        try
        {
            ApplyInstalledApps(await installedTask.ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            installedRefreshError = ex;
        }

        await updatesTask.ConfigureAwait(true);
        StatusMessage = installedRefreshError is null
            ? "Installed packages and updates were refreshed."
            : $"The operation finished, but installed package refresh failed: {installedRefreshError.Message}";
    }

    private void ApplyUpdateSnapshot(UpdateSnapshot? snapshot, UpdateCheckState state)
    {
        if (snapshot is not null)
        {
            ReplaceAll(AvailableUpdates, snapshot.Updates);
        }

        LastUpdateCheckAt = snapshot?.LastAttemptAt;
        UpdateCheckError = snapshot?.LastError;
        UpdatesCheckState = state;
    }

    private void ApplyQueueSnapshot(OperationQueueSnapshot snapshot)
    {
        CurrentOperation = snapshot.Current;
        ReplaceAll(PendingOperations, snapshot.Pending);
        ReplaceAll(OperationHistory, snapshot.History);
        OnPropertyChanged(nameof(HasActiveOperation));
        OnPropertyChanged(nameof(ActivitySummary));
    }

    private async Task<InstalledLoadResult> LoadInstalledAppsAsync(CancellationToken cancellationToken)
    {
        if (_installedAppInventory is null)
        {
            var packages = await _wingetClient.GetInstalledPackagesAsync(cancellationToken).ConfigureAwait(false);
            return new InstalledLoadResult(packages, [], []);
        }

        var snapshot = await _installedAppInventory.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var wingetPackages = snapshot.Apps
            .SelectMany(app => app.Installations
                .Where(installation => installation.WingetPackage is not null)
                .Select(installation => new PackageSummary
                {
                    Key = installation.WingetPackage!,
                    Name = app.Name,
                    Publisher = app.Publisher,
                    InstalledVersion = installation.Version,
                    SourceName = installation.ProviderId,
                    Status = PackageStatus.Installed
                }))
            .GroupBy(package => package.Key)
            .Select(group => group.First())
            .ToArray();
        return new InstalledLoadResult(wingetPackages, snapshot.Apps, snapshot.Providers);
    }

    private void ApplyInstalledApps(InstalledLoadResult result)
    {
        ReplaceAll(InstalledPackages, result.WingetPackages);
        ReplaceAll(InstalledApps, result.Apps);
        ReplaceAll(InstalledAppProviders, result.Providers);
    }

    private void LoadSourceAgreementConsents()
    {
        const string prefix = "sourceAgreementConsent:";
        foreach (var setting in ApplicationData.Current.LocalSettings.Values)
        {
            if (setting.Key.StartsWith(prefix, StringComparison.Ordinal)
                && setting.Value is string fingerprint
                && !string.IsNullOrWhiteSpace(fingerprint))
            {
                _acceptedSourceAgreementFingerprints[setting.Key[prefix.Length..]] = fingerprint;
            }
        }

        HasAcceptedSourceAgreements = _acceptedSourceAgreementFingerprints.Count > 0;
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static string FormatOperationKind(PackageOperationKind kind) => kind switch
    {
        PackageOperationKind.Install => "Installing",
        PackageOperationKind.Upgrade => "Updating",
        PackageOperationKind.Uninstall => "Removing",
        _ => "Processing"
    };

    private static string FormatState(PackageOperationState state) => state.ToString().Replace("RebootRequired", "reboot required");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _operationQueue.Changed -= OnOperationQueueChanged;
    }

    private sealed record InstalledLoadResult(
        IReadOnlyList<PackageSummary> WingetPackages,
        IReadOnlyList<InstalledApp> Apps,
        IReadOnlyList<InstalledAppProviderStatus> Providers);
}
