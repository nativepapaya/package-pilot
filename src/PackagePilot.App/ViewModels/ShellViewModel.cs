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
    private const string PendingMutationVerificationsFileName =
        "pending-mutation-verifications.json";
    private readonly IWingetClient _wingetClient;
    private readonly IOperationQueue _operationQueue;
    private readonly IUpdateCoordinator _updateCoordinator;
    private readonly UpdateScanWorker _updateScanWorker;
    private readonly IMutationVerificationStore _mutationVerificationStore;
    private readonly MutationOperationAdmissionService _mutationAdmissionService;
    private readonly IInstalledAppInventory? _installedAppInventory;
    private readonly IInstalledAppSnapshotStore? _installedAppSnapshotStore;
    private readonly ISourceManagementService? _sourceManagementService;
    private readonly IAppLifetimeActivityGate _lifetimeActivityGate;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<UpdateMonitoringCadence> _getUpdateMonitoringCadence;
    private readonly IWindowActivityService? _windowActivityService;
    private readonly bool _elevatedPackageOperationsAvailable;
    private readonly string? _bootSessionIdentityError;
    private readonly HashSet<Guid> _observedCompletions = [];
    private readonly MutationVerificationTracker _mutationVerificationTracker;
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
    private bool _isSearching;
    private bool _isSearchTruncated;
    private bool _isDetailsLoading;
    private bool _hasAcceptedSourceAgreements;
    private UpdateCheckState _updatesCheckState = UpdateCheckState.NotChecked;
    private DateTimeOffset? _lastUpdateCheckAt;
    private DateTimeOffset? _lastSuccessfulUpdateCheckAt;
    private string? _updateCheckError;
    private SourceManagementCapabilities _sourceManagementCapabilities = new();
    private WindowsIntegrationCapabilities _windowsIntegrationCapabilities = new();
    private bool _isManagingSources;
    private int _operationBatchDepth;
    private long _mutationGeneration;
    private bool _mutationRefreshPending;
    private bool _mutationRefreshRunning;
    private bool _operationHistoryInitialized;
    private bool _mutationVerificationLoadFailed;
    private bool _mutationVerificationStoreInitialized;
    private bool _isInstalledInventoryAuthoritative;
    private string? _mutationVerificationPersistenceError;
    private bool _disposed;

    public ShellViewModel(
        IWingetClient wingetClient,
        IOperationQueue operationQueue,
        DispatcherQueue dispatcher,
        IUpdateCoordinator? updateCoordinator = null,
        UpdateScanWorker? updateScanWorker = null,
        IInstalledAppInventory? installedAppInventory = null,
        IInstalledAppSnapshotStore? installedAppSnapshotStore = null,
        ISourceManagementService? sourceManagementService = null,
        bool notificationRegistrationSupported = false,
        bool notificationRegistered = false,
        BackgroundMonitoringState backgroundMonitoringState = BackgroundMonitoringState.Disabled,
        Func<UpdateMonitoringCadence>? getUpdateMonitoringCadence = null,
        IWindowActivityService? windowActivityService = null,
        IAppLifetimeActivityGate? lifetimeActivityGate = null,
        IMutationVerificationStore? mutationVerificationStore = null,
        string? currentBootSessionId = null,
        string? bootSessionIdentityError = null,
        bool elevatedPackageOperationsAvailable = false)
    {
        _wingetClient = wingetClient;
        _operationQueue = operationQueue;
        _dispatcher = dispatcher;
        _installedAppInventory = installedAppInventory;
        _installedAppSnapshotStore = installedAppSnapshotStore;
        _sourceManagementService = sourceManagementService;
        _lifetimeActivityGate = lifetimeActivityGate ?? new AppLifetimeActivityGate();
        _getUpdateMonitoringCadence = getUpdateMonitoringCadence
            ?? (() => UpdateMonitoringCadence.Daily);
        _windowActivityService = windowActivityService;
        _elevatedPackageOperationsAvailable = elevatedPackageOperationsAvailable;
        _bootSessionIdentityError = string.IsNullOrWhiteSpace(currentBootSessionId)
            ? bootSessionIdentityError
                ?? "Package Pilot could not identify the current Windows boot session."
            : null;
        _mutationVerificationTracker = new MutationVerificationTracker(
            currentBootSessionId);
        _mutationVerificationStore = mutationVerificationStore
            ?? new JsonMutationVerificationStore(Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                PendingMutationVerificationsFileName));
        _mutationAdmissionService = new MutationOperationAdmissionService(
            _operationQueue,
            _mutationVerificationTracker,
            _mutationVerificationStore);
        _windowsIntegrationCapabilities = new WindowsIntegrationCapabilities
        {
            NotificationRegistrationSupported = notificationRegistrationSupported,
            NotificationRegistered = notificationRegistered,
            BackgroundMonitoringState = backgroundMonitoringState
        };
        LoadSourceAgreementConsents();
        LoadPendingMutationVerifications();
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

    public bool IsInstalledInventoryAuthoritative
    {
        get => _isInstalledInventoryAuthoritative;
        private set => SetProperty(ref _isInstalledInventoryAuthoritative, value);
    }

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
                OnPropertyChanged(nameof(CanQueuePackageMutations));
                OnPropertyChanged(nameof(CanRetryPackageAsAdministrator));
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

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
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

    public DateTimeOffset? LastSuccessfulUpdateCheckAt
    {
        get => _lastSuccessfulUpdateCheckAt;
        private set => SetProperty(ref _lastSuccessfulUpdateCheckAt, value);
    }

    public string? UpdateCheckError
    {
        get => _updateCheckError;
        private set => SetProperty(ref _updateCheckError, value);
    }

    public string? MutationVerificationPersistenceError
    {
        get => _mutationVerificationPersistenceError;
        private set
        {
            if (SetProperty(ref _mutationVerificationPersistenceError, value))
            {
                OnPropertyChanged(nameof(CanQueuePackageMutations));
                OnPropertyChanged(nameof(CanRetryPackageAsAdministrator));
            }
        }
    }

    public bool CanQueuePackageMutations =>
        _operationHistoryInitialized
        && IsReady
        && !_mutationVerificationLoadFailed
        && _mutationVerificationTracker.CanAdmitMutations
        && string.IsNullOrWhiteSpace(MutationVerificationPersistenceError);

    public bool CanRetryPackageAsAdministrator =>
        _elevatedPackageOperationsAvailable && CanQueuePackageMutations;

    public bool CanRetryPackageAsAdministratorFor(
        PackageKey package,
        PackageOperationKind kind)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!CanRetryPackageAsAdministrator
            || kind is not (PackageOperationKind.Install or PackageOperationKind.Upgrade))
        {
            return false;
        }

        var latest = OperationHistory
            .Where(result => result.EffectiveTarget is WingetTarget target
                && target.Package == package)
            .OrderByDescending(result => result.CompletedAt)
            .FirstOrDefault();
        return latest is not null
            && latest.Kind == kind
            && latest.State is PackageOperationState.Failed or PackageOperationState.Cancelled
            && (latest.Error?.Kind == WingetErrorKind.AdministratorRequired
                || latest.AdministratorRetryRequested);
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
        var scheduleMutationVerification = false;
        try
        {
            _updateSnapshot = await _updateCoordinator.LoadAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
            ApplyUpdateSnapshot(
                _updateSnapshot,
                _updateCoordinator.GetState(
                    _updateSnapshot,
                    _getUpdateMonitoringCadence()));

            if (_installedAppSnapshotStore is not null
                && await _installedAppSnapshotStore.LoadAsync(
                    _lifetimeCancellation.Token).ConfigureAwait(true) is { } cachedInstalled)
            {
                ApplyInstalledApps(CreateInstalledLoadResult(
                    cachedInstalled,
                    isAuthoritative: false));
            }

            await _operationQueue.Initialization.ConfigureAwait(true);
            var initialQueueSnapshot = _operationQueue.Snapshot;
            foreach (var result in initialQueueSnapshot.History)
            {
                _observedCompletions.Add(result.OperationId);
            }
            ApplyQueueSnapshot(initialQueueSnapshot);
            scheduleMutationVerification = SeedPendingMutationVerifications(initialQueueSnapshot);
            _operationHistoryInitialized = true;
            OnPropertyChanged(nameof(CanQueuePackageMutations));
            OnPropertyChanged(nameof(CanRetryPackageAsAdministrator));

            Capabilities = await _wingetClient.GetCapabilitiesAsync(_lifetimeCancellation.Token).ConfigureAwait(true);
            if (!IsReady)
            {
                StatusMessage = Capabilities.UnavailableReason ?? "WinGet is unavailable.";
                return;
            }

            var installedVerificationTargets = GetInstalledVerificationTargetsForCurrentBoot();
            var installedVerificationGeneration = _mutationGeneration;
            var installedTask = LoadInstalledAppsAsync(_lifetimeCancellation.Token);
            var sourcesTask = LoadSourceStatusesAsync();
            await Task.WhenAll(installedTask, sourcesTask).ConfigureAwait(true);

            var installed = await installedTask.ConfigureAwait(true);
            ApplyInstalledApps(installed);
            CompleteInstalledMutationVerification(
                installed,
                installedVerificationTargets,
                installedVerificationGeneration);
            if (await sourcesTask.ConfigureAwait(true) is { } sources)
            {
                ReplaceAll(SourceStatuses, sources);
            }
            scheduleMutationVerification =
                _mutationVerificationTracker.HasUpgradeTargetsEligibleForStartupVerification;
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

        if (scheduleMutationVerification && !_disposed)
        {
            _ = RefreshUpdatesCoreAsync(UpdateCheckReason.PackageMutation);
        }
        else if (scheduleAutomaticUpdateCheck && !_disposed)
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
            IsSearching = false;
            IsBusy = false;
            SearchResults.Clear();
            IsSearchTruncated = false;
            StatusMessage = searchText.Length == 0 ? "Search WinGet to discover packages." : "Type at least two characters.";
            return;
        }

        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(true);
            IsBusy = true;
            IsSearching = true;
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
                IsSearching = false;
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
            var installedVerificationTargets = GetInstalledVerificationTargetsForCurrentBoot();
            var installedVerificationGeneration = _mutationGeneration;
            var installedTask = LoadInstalledAppsAsync(_lifetimeCancellation.Token);
            var sourcesTask = LoadSourceStatusesAsync();
            var updatesTask = RefreshUpdatesCoreAsync(UpdateCheckReason.Manual);
            await Task.WhenAll(installedTask, sourcesTask).ConfigureAwait(true);
            var installed = await installedTask.ConfigureAwait(true);
            ApplyInstalledApps(installed);
            CompleteInstalledMutationVerification(
                installed,
                installedVerificationTargets,
                installedVerificationGeneration);
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
            var verificationTargets = GetInstalledVerificationTargetsForCurrentBoot();
            var mutationGeneration = _mutationGeneration;
            var installed = await LoadInstalledAppsAsync(
                _lifetimeCancellation.Token).ConfigureAwait(true);
            ApplyInstalledApps(installed);
            CompleteInstalledMutationVerification(
                installed,
                verificationTargets,
                mutationGeneration);
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

    public async Task RefreshUpdatesAsync() =>
        await RefreshUpdatesCoreAsync(UpdateCheckReason.Manual).ConfigureAwait(true);

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

    public string? GetAcceptedSourceAgreementFingerprint(string sourceId) =>
        IsSourceAgreementAccepted(sourceId)
            ? _acceptedSourceAgreementFingerprints.GetValueOrDefault(sourceId)
            : null;

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
        PackageArchitecture architecture = PackageArchitecture.Unknown,
        string? acceptedSourceAgreementFingerprint = null,
        string? acceptedPackageAgreementFingerprint = null,
        bool runAsAdministrator = false)
    {
        var result = EnqueueOperations([
            new PackageMutationRequest(
                package,
                kind,
                acceptedSourceAgreements,
                acceptedPackageAgreements,
                scope,
                architecture,
                acceptedSourceAgreementFingerprint,
                acceptedPackageAgreementFingerprint,
                runAsAdministrator)
        ]);
        return result.OperationIds.Single();
    }

    public IReadOnlyList<Guid> EnqueueUpdates(IEnumerable<PackageSummary> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return EnqueueOperations(packages.Select(package =>
            new PackageMutationRequest(package, PackageOperationKind.Upgrade))).OperationIds;
    }

    public MutationAdmissionBatchResult EnqueueOperations(
        IEnumerable<PackageMutationRequest> requests,
        bool skipDuplicates = false)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (!CanQueuePackageMutations)
        {
            throw CreateMutationRecoveryUnavailableException();
        }

        var admissions = requests.Select(request =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Package);
            var preferences = new InstallPreferences
            {
                AcceptSourceAgreements = request.AcceptedSourceAgreements,
                AcceptedSourceAgreementFingerprint = request.AcceptedSourceAgreements
                    ? request.AcceptedSourceAgreementFingerprint
                    : null,
                AcceptPackageAgreements = request.AcceptedPackageAgreements,
                AcceptedPackageAgreementFingerprint = request.AcceptedPackageAgreements
                    ? request.AcceptedPackageAgreementFingerprint
                    : null,
                Scope = request.Scope,
                Architecture = request.Architecture
            };
            return new MutationAdmission(
                PackageOperation.Create(
                    request.Kind,
                    request.Package.Key,
                    request.Package.Name,
                    preferences) with
                    {
                        RunAsAdministrator = request.RunAsAdministrator
                    },
                request.Package);
        }).ToArray();

        BeginOperationBatch();
        try
        {
            var result = _mutationAdmissionService.Enqueue(admissions, skipDuplicates);
            _mutationGeneration += result.OperationIds.Count;
            NotifyMutationVerificationStateChanged();
            return result;
        }
        catch (MutationRecoveryUnavailableException exception)
        {
            MutationVerificationPersistenceError =
                FormatRecoveryError(exception.InnerException ?? exception);
            NotifyMutationVerificationStateChanged();
            throw;
        }
        catch
        {
            NotifyMutationVerificationStateChanged();
            throw;
        }
        finally
        {
            EndOperationBatch();
        }
    }

    public bool TryCancelOperation(Guid operationId) => _operationQueue.TryCancel(operationId);

    public OperationQueueSnapshot OperationQueueSnapshot => _operationQueue.Snapshot;

    public int PendingMutationVerificationCount => _mutationVerificationTracker.Count;

    public IReadOnlyList<MutationVerificationMarker> PendingMutationVerifications =>
        _mutationVerificationTracker.Export();

    public IReadOnlyList<PackageSummary> PendingUpgradeVerifications =>
        _mutationVerificationTracker.GetPendingUpgradeVerifications();

    public IReadOnlyList<PackageSummary> RestartRequiredUpdates =>
        _mutationVerificationTracker.GetRestartRequiredUpdatesForCurrentBoot();

    public bool IsPackageMutationBlocked(PackageKey package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (_mutationVerificationTracker.Contains(package))
        {
            return true;
        }

        var snapshot = _operationQueue.Snapshot;
        return (snapshot.Current is { } current
                && IsMatchingWingetOperation(current.Operation, package))
            || snapshot.Pending.Any(entry =>
                IsMatchingWingetOperation(entry.Operation, package));
    }

    public bool IsMutationVerificationPending(PackageKey package) =>
        _mutationVerificationTracker.Contains(package);

    public bool IsRestartRequiredThisBoot(PackageKey package) =>
        _mutationVerificationTracker.IsRestartRequiredThisBoot(package);

    public MutationVerificationPhase? GetMutationVerificationPhase(PackageKey package) =>
        _mutationVerificationTracker.GetPhase(package);

    public MutationVerificationPhase? GetMutationVerificationPhase(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.EffectiveTarget is WingetTarget target
            ? _mutationVerificationTracker.GetPhase(target.Package, result.OperationId)
            : null;
    }

    public void BeginOperationBatch() => _operationBatchDepth++;

    public void EndOperationBatch()
    {
        if (_operationBatchDepth <= 0)
        {
            throw new InvalidOperationException("No operation batch is active.");
        }

        _operationBatchDepth--;
        if (_operationBatchDepth == 0)
        {
            TryStartMutationRefresh();
        }
    }

    public Guid EnqueueMsixRemoval(InstalledApp app, string packageFullName)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        if (!CanQueuePackageMutations)
        {
            throw CreateMutationRecoveryUnavailableException();
        }

        var installation = app.Installations.FirstOrDefault(item =>
            string.Equals(item.PackageFullName, packageFullName, StringComparison.OrdinalIgnoreCase));
        var familyName = installation?.Aliases
            .FirstOrDefault(alias => alias.Kind == InstalledAppAliasKind.PackageFamilyName)?.Value
            ?? string.Empty;
        var operationId = _operationQueue.Enqueue(new PackageOperation
        {
            Kind = PackageOperationKind.Uninstall,
            DisplayName = app.Name,
            Target = new MsixTarget
            {
                PackageFullName = packageFullName,
                PackageFamilyName = familyName
            }
        });
        _mutationGeneration++;
        return operationId;
    }

    public bool ClearHistory()
    {
        if (!CanQueuePackageMutations)
        {
            StatusMessage = "Activity history was kept because package-operation recovery state is unavailable.";
            return false;
        }

        _operationQueue.ClearHistory();
        return true;
    }

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

    private async Task<UpdateCheckResult?> RefreshUpdatesCoreAsync(
        UpdateCheckReason reason,
        Task<InstalledLoadResult>? installedVerificationTask = null)
    {
        if (!IsReady || _disposed)
        {
            return null;
        }

        var effectiveReason = _mutationVerificationTracker.GetEffectiveCheckReason(reason);
        var verificationTargets = effectiveReason == UpdateCheckReason.PackageMutation
            ? GetVerificationTargetsForCurrentBoot(PackageOperationKind.Upgrade)
            : Array.Empty<MutationVerificationTarget>();
        var mutationGeneration = _mutationGeneration;
        UpdatesCheckState = UpdateCheckState.Checking;
        UpdateCheckError = null;
        try
        {
            var execution = _windowActivityService is null
                ? await _updateScanWorker.RunAsync(
                    effectiveReason,
                    isForegroundWindowActive: true,
                    SourceStatuses.ToArray(),
                    _lifetimeCancellation.Token,
                    _getUpdateMonitoringCadence()).ConfigureAwait(true)
                : await _updateScanWorker.RunAsync(
                    effectiveReason,
                    _windowActivityService,
                    SourceStatuses.ToArray(),
                    _lifetimeCancellation.Token,
                    _getUpdateMonitoringCadence()).ConfigureAwait(true);
            var result = execution.Check;

            if (effectiveReason == UpdateCheckReason.PackageMutation
                && (mutationGeneration != _mutationGeneration
                    || !_operationQueue.Snapshot.IsIdle))
            {
                _mutationRefreshPending = true;
                ApplyUpdateSnapshot(
                    _updateSnapshot,
                    _updateCoordinator.GetState(
                        _updateSnapshot,
                        _getUpdateMonitoringCadence()));
                return result;
            }

            if (effectiveReason != UpdateCheckReason.PackageMutation
                && ShouldSuppressNonMutationSnapshot())
            {
                ApplyUpdateSnapshot(
                    _updateSnapshot,
                    _updateCoordinator.GetState(
                        _updateSnapshot,
                        _getUpdateMonitoringCadence()));
                return result;
            }

            _updateSnapshot = result.Snapshot;
            ApplyUpdateSnapshot(result.Snapshot, result.State);
            if (effectiveReason == UpdateCheckReason.PackageMutation
                && result.State == UpdateCheckState.Current)
            {
                InstalledLoadResult installedVerification;
                try
                {
                    installedVerification = installedVerificationTask is null
                        ? await LoadInstalledAppsAsync(_lifetimeCancellation.Token).ConfigureAwait(true)
                        : await installedVerificationTask.ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    installedVerification = new InstalledLoadResult(
                        [],
                        [],
                        [],
                        IsWingetInventoryHealthy: false,
                        IsAuthoritative: false);
                    UpdateCheckError =
                        $"Installed-version verification was unavailable: {exception.Message}";
                }

                var reconciliation = await ReconcileUpgradeMutationVerificationAsync(
                    verificationTargets,
                    result.Snapshot.Updates,
                    installedVerification.WingetPackages,
                    installedVerification.IsWingetInventoryHealthy).ConfigureAwait(true);
                if (reconciliation.NoChangeDetected.Count > 0)
                {
                    StatusMessage =
                        "WinGet reported completion, but repeated checks found no installed-version change. Close the app completely, then retry.";
                }
                else if (reconciliation.NoChangeFinalizationFailed.Count > 0)
                {
                    StatusMessage =
                        "The installed version did not change, but Package Pilot could not safely update Activity. The verification lock was kept; resolve the Activity storage error, then check again.";
                }
                else if (reconciliation.ApplicationRestartPending.Count > 0)
                {
                    StatusMessage =
                        "WinGet reported completion, but Windows still reports the previous version. If the app was open, close and reopen it, then check again.";
                }
                else if (reconciliation.Inconclusive.Count > 0)
                {
                    StatusMessage =
                        "WinGet finished, but Package Pilot still cannot verify the installed version. Check again later.";
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            UpdatesCheckState = UpdateCheckState.Failed;
            UpdateCheckError = ex.Message;
            return null;
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
            if (!_operationHistoryInitialized)
            {
                return;
            }

            var verificationStateChanged = false;
            foreach (var result in e.Snapshot.History.Where(result =>
                         _observedCompletions.Add(result.OperationId)))
            {
                verificationStateChanged |=
                    _mutationVerificationTracker.RecordResult(result);

                if (result.IsSuccess)
                {
                    _mutationRefreshPending = true;
                }
            }

            if (verificationStateChanged)
            {
                OnMutationVerificationStateChanged();
            }

            TryStartMutationRefresh();
        });
    }

    private void TryStartMutationRefresh()
    {
        var snapshot = _operationQueue.Snapshot;
        if (_disposed
            || !IsReady
            || !snapshot.IsIdle
            || _operationBatchDepth > 0
            || !_mutationRefreshPending
            || _mutationRefreshRunning)
        {
            return;
        }

        _mutationRefreshPending = false;
        _mutationRefreshRunning = true;
        _ = RefreshAfterMutationAsync();
    }

    private async Task RefreshAfterMutationAsync()
    {
        try
        {
            Exception? installedRefreshError = null;
            var installedVerificationTargets = GetInstalledVerificationTargetsForCurrentBoot();
            var installedVerificationGeneration = _mutationGeneration;
            var installedTask = LoadInstalledAppsAsync(_lifetimeCancellation.Token);
            var updatesTask = RefreshUpdatesCoreAsync(
                UpdateCheckReason.PackageMutation,
                installedTask);

            try
            {
                var installed = await installedTask.ConfigureAwait(true);
                ApplyInstalledApps(installed);
                CompleteInstalledMutationVerification(
                    installed,
                    installedVerificationTargets,
                    installedVerificationGeneration);
            }
            catch (Exception ex)
            {
                installedRefreshError = ex;
            }

            var updateCheck = await updatesTask.ConfigureAwait(true);
            StatusMessage = installedRefreshError is not null
                ? $"The operation finished, but installed package refresh failed: {installedRefreshError.Message}"
                : _mutationVerificationTracker.HasApplicationRestartPending
                    ? "WinGet reported completion, but Windows still reports the previous version. If the app was open, close and reopen it, then check again."
                : _mutationVerificationTracker.HasUpgradeTargetsEligibleForVerification
                    ? "The operation finished, but installed-version verification is still pending."
                : updateCheck?.State == UpdateCheckState.Current
                    ? "Installed packages and updates were refreshed."
                    : "The operation finished, but update verification is still pending.";
        }
        finally
        {
            _mutationRefreshRunning = false;
            TryStartMutationRefresh();
        }
    }

    private void ApplyUpdateSnapshot(UpdateSnapshot? snapshot, UpdateCheckState state)
    {
        if (snapshot is not null)
        {
            ReplaceAll(AvailableUpdates, snapshot.Updates);
        }

        LastUpdateCheckAt = snapshot?.LastAttemptAt;
        LastSuccessfulUpdateCheckAt = snapshot?.LastSuccessAt;
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

    private static bool IsMatchingWingetOperation(
        PackageOperation operation,
        PackageKey package) =>
        operation.EffectiveTarget is WingetTarget target
        && target.Package == package;

    private bool SeedPendingMutationVerifications(OperationQueueSnapshot queue)
    {
        if (!_mutationVerificationStoreInitialized)
        {
            _mutationVerificationTracker.SeedVerifiedOperations(queue.History);
            _mutationVerificationStoreInitialized = PersistPendingMutationVerifications();
            NotifyMutationVerificationStateChanged();
            return false;
        }

        if (_mutationVerificationTracker.ReconcileHistory(
                queue.History,
                _updateSnapshot?.Updates ?? Array.Empty<PackageSummary>()))
        {
            OnMutationVerificationStateChanged();
        }
        return _mutationVerificationTracker.HasUpgradeTargetsEligibleForVerification;
    }

    private MutationVerificationTarget[] GetVerificationTargetsForCurrentBoot(
        PackageOperationKind kind) =>
        _mutationVerificationTracker.CaptureVerificationTargetsForCurrentBoot(kind);

    private MutationVerificationTarget[] GetInstalledVerificationTargetsForCurrentBoot() =>
        _mutationVerificationTracker.CaptureVerificationTargetsForCurrentBoot()
            .Where(target => target.Kind is
                PackageOperationKind.Install or PackageOperationKind.Uninstall)
            .ToArray();

    private bool ShouldSuppressNonMutationSnapshot() =>
        _mutationVerificationTracker.HasUpgradeTargetsEligibleForVerification;

    private void CompleteMutationVerification(IEnumerable<MutationVerificationTarget> targets)
    {
        if (_mutationVerificationTracker.CompleteVerification(targets))
        {
            OnMutationVerificationStateChanged();
        }
    }

    private async Task<UpgradeVerificationReconciliation> ReconcileUpgradeMutationVerificationAsync(
        IEnumerable<MutationVerificationTarget> targets,
        IEnumerable<PackageSummary> currentUpdates,
        IEnumerable<PackageSummary> currentInstalled,
        bool isInstalledInventoryHealthy)
    {
        var reconciliation = _mutationVerificationTracker.ReconcileUpgradeVerification(
            targets,
            currentUpdates,
            currentInstalled,
            isInstalledInventoryHealthy);
        var finalized = new List<MutationVerificationTarget>();
        var finalizationFailed = new List<MutationVerificationTarget>();
        var stateChanged = reconciliation.StateChanged;
        foreach (var target in reconciliation.NoChangeDetected)
        {
            if (await _operationQueue.TryMarkUpgradeNoChangeDetectedAsync(
                    target.OperationId,
                    target.Package).ConfigureAwait(true))
            {
                finalized.Add(target);
                stateChanged |= _mutationVerificationTracker.CompleteVerification([target]);
            }
            else
            {
                finalizationFailed.Add(target);
            }
        }

        if (stateChanged)
        {
            OnMutationVerificationStateChanged();
        }

        return reconciliation with
        {
            NoChangeDetected = finalized,
            NoChangeFinalizationFailed = finalizationFailed,
            StateChanged = stateChanged
        };
    }

    private void CompleteInstalledMutationVerification(
        InstalledLoadResult installed,
        IReadOnlyCollection<MutationVerificationTarget> targets,
        long mutationGeneration)
    {
        // Only a complete, exact WinGet inventory can prove the post-operation state
        // of Install and Uninstall targets. The revision and generation checks prevent
        // a scan that started before newer work from clearing a newer recovery marker.
        if (!installed.IsWingetInventoryHealthy
            || targets.Count == 0
            || mutationGeneration != _mutationGeneration
            || !_operationQueue.Snapshot.IsIdle)
        {
            return;
        }

        CompleteMutationVerification(targets);
    }

    private void LoadPendingMutationVerifications()
    {
        try
        {
            var snapshot = _mutationVerificationStore.Load();
            if (snapshot is not null)
            {
                _mutationVerificationTracker.Import(snapshot);
                _mutationVerificationStoreInitialized = true;
            }
            _mutationVerificationLoadFailed = false;
            MutationVerificationPersistenceError = _bootSessionIdentityError;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _mutationVerificationLoadFailed = true;
            MutationVerificationPersistenceError = FormatRecoveryError(exception);
        }
    }

    private void OnMutationVerificationStateChanged()
    {
        PersistPendingMutationVerifications();
        NotifyMutationVerificationStateChanged();
    }

    private void NotifyMutationVerificationStateChanged()
    {
        OnPropertyChanged(nameof(PendingMutationVerificationCount));
        OnPropertyChanged(nameof(PendingMutationVerifications));
        OnPropertyChanged(nameof(PendingUpgradeVerifications));
        OnPropertyChanged(nameof(RestartRequiredUpdates));
    }

    private bool PersistPendingMutationVerifications()
    {
        if (_mutationVerificationLoadFailed)
        {
            return false;
        }

        try
        {
            _mutationVerificationStore.Save(_mutationVerificationTracker.CreateSnapshot());
            _mutationVerificationStoreInitialized = true;
            MutationVerificationPersistenceError = _bootSessionIdentityError;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MutationVerificationPersistenceError = FormatRecoveryError(exception);
            StatusMessage = "Package Pilot could not save package-operation recovery state. Keep the app open while operations finish and check local storage access.";
            return false;
        }
    }

    private static string FormatRecoveryError(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message)
            ? "Package-operation recovery state is unavailable."
            : exception.Message;

    private MutationRecoveryUnavailableException CreateMutationRecoveryUnavailableException() =>
        new(
            "Package Pilot paused package changes because it cannot safely read or write operation recovery state. Restart the app after checking local storage access.");

    private Guid FindLatestOperationId(PackageKey package)
    {
        var snapshot = _operationQueue.Snapshot;
        if (snapshot.Current is { } current
            && IsMatchingWingetOperation(current.Operation, package))
        {
            return current.Operation.Id;
        }

        var pending = snapshot.Pending.FirstOrDefault(entry =>
            IsMatchingWingetOperation(entry.Operation, package));
        if (pending is not null)
        {
            return pending.Operation.Id;
        }

        return snapshot.History
            .FirstOrDefault(result => result.EffectiveTarget is WingetTarget target
                && target.Package == package)?.OperationId ?? Guid.Empty;
    }

    private async Task<InstalledLoadResult> LoadInstalledAppsAsync(CancellationToken cancellationToken)
    {
        if (_installedAppInventory is null)
        {
            var packages = await _wingetClient.GetInstalledPackagesAsync(cancellationToken).ConfigureAwait(false);
            return new InstalledLoadResult(
                packages,
                [],
                [],
                IsWingetInventoryHealthy: true,
                IsAuthoritative: true);
        }

        var snapshot = await _installedAppInventory.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return CreateInstalledLoadResult(snapshot, isAuthoritative: true);
    }

    private static InstalledLoadResult CreateInstalledLoadResult(
        InstalledAppSnapshot snapshot,
        bool isAuthoritative)
    {
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
        var wingetInventoryHealthy = snapshot.Providers.Any(provider =>
            provider.Provider == InstalledAppProviderKind.Winget
            && provider.Health == InventoryProviderHealth.Healthy);
        return new InstalledLoadResult(
            wingetPackages,
            snapshot.Apps,
            snapshot.Providers,
            wingetInventoryHealthy,
            isAuthoritative);
    }

    private void ApplyInstalledApps(InstalledLoadResult result)
    {
        ReplaceAll(InstalledPackages, result.WingetPackages);
        ReplaceAll(InstalledApps, result.Apps);
        ReplaceAll(InstalledAppProviders, result.Providers);
        IsInstalledInventoryAuthoritative = result.IsAuthoritative;
        if (result.IsAuthoritative && _installedAppSnapshotStore is not null)
        {
            _ = PersistInstalledSnapshotAsync(result);
        }
    }

    private async Task PersistInstalledSnapshotAsync(InstalledLoadResult result)
    {
        try
        {
            await _installedAppSnapshotStore!.SaveAsync(
                new InstalledAppSnapshot
                {
                    CapturedAt = DateTimeOffset.UtcNow,
                    Apps = result.Apps,
                    Providers = result.Providers
                },
                _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Display caching is an optimization and never gates live inventory or mutations.
        }
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
        IReadOnlyList<InstalledAppProviderStatus> Providers,
        bool IsWingetInventoryHealthy,
        bool IsAuthoritative);
}

public sealed record PackageMutationRequest(
    PackageSummary Package,
    PackageOperationKind Kind,
    bool AcceptedSourceAgreements = false,
    bool AcceptedPackageAgreements = false,
    InstallerScope Scope = InstallerScope.Unknown,
    PackageArchitecture Architecture = PackageArchitecture.Unknown,
    string? AcceptedSourceAgreementFingerprint = null,
    string? AcceptedPackageAgreementFingerprint = null,
    bool RunAsAdministrator = false);
