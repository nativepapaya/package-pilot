using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.App.ViewModels;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IWingetClient _wingetClient;
    private readonly IOperationQueue _operationQueue;
    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<Guid> _observedCompletions = [];
    private CancellationTokenSource? _searchCancellation;
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
    private bool _disposed;

    public ShellViewModel(
        IWingetClient wingetClient,
        IOperationQueue operationQueue,
        DispatcherQueue dispatcher)
    {
        _wingetClient = wingetClient;
        _operationQueue = operationQueue;
        _dispatcher = dispatcher;
        _operationQueue.Changed += OnOperationQueueChanged;

        InitializeCommand = new AsyncRelayCommand(InitializeAsync);
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        RefreshCommand = new AsyncRelayCommand(RefreshAllAsync, () => IsReady && !IsBusy);
        SelectPackageCommand = new AsyncRelayCommand<PackageSummary?>(SelectPackageAsync);
    }

    public ObservableCollection<PackageSummary> SearchResults { get; } = [];
    public ObservableCollection<PackageSummary> InstalledPackages { get; } = [];
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

        try
        {
            await _operationQueue.Initialization.ConfigureAwait(true);
            ApplyQueueSnapshot(_operationQueue.Snapshot);

            Capabilities = await _wingetClient.GetCapabilitiesAsync().ConfigureAwait(true);
            if (!IsReady)
            {
                StatusMessage = Capabilities.UnavailableReason ?? "WinGet is unavailable.";
                return;
            }

            await RefreshAllCoreAsync().ConfigureAwait(true);
            StatusMessage = "Ready";
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
    }

    public bool HasAcceptedSourceAgreements
    {
        get => _hasAcceptedSourceAgreements;
        private set => SetProperty(ref _hasAcceptedSourceAgreements, value);
    }

    public Task SearchAsync() => SearchCoreAsync(HasAcceptedSourceAgreements);

    public Task SearchAcceptingSourceAgreementsAsync() =>
        SearchCoreAsync(acceptSourceAgreements: true);

    private async Task SearchCoreAsync(bool acceptSourceAgreements)
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
                    AcceptSourceAgreements = acceptSourceAgreements
                },
                cancellationToken).ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();

            ReplaceAll(SearchResults, result.Packages);
            ReplaceAll(SourceStatuses, result.Sources);
            if (acceptSourceAgreements)
            {
                HasAcceptedSourceAgreements = true;
            }
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
            await RefreshAllCoreAsync().ConfigureAwait(true);
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

    public Task SelectPackageAsync(PackageSummary? package) =>
        SelectPackageCoreAsync(package, HasAcceptedSourceAgreements);

    public Task SelectPackageAcceptingSourceAgreementsAsync(PackageSummary package) =>
        SelectPackageCoreAsync(package, acceptSourceAgreements: true);

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
                new InstallPreferences { AcceptSourceAgreements = acceptSourceAgreements }).ConfigureAwait(true);
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

    public void ClearHistory() => _operationQueue.ClearHistory();

    public void SetStatusMessage(string message) => StatusMessage = message;

    private async Task RefreshAllCoreAsync()
    {
        var installedTask = _wingetClient.GetInstalledPackagesAsync();
        var updatesTask = _wingetClient.GetAvailableUpdatesAsync();
        var sourcesTask = _wingetClient.GetSourcesAsync();
        await Task.WhenAll(installedTask, updatesTask, sourcesTask).ConfigureAwait(true);

        ReplaceAll(InstalledPackages, await installedTask.ConfigureAwait(true));
        ReplaceAll(AvailableUpdates, await updatesTask.ConfigureAwait(true));
        ReplaceAll(SourceStatuses, await sourcesTask.ConfigureAwait(true));
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
        try
        {
            await RefreshAllCoreAsync().ConfigureAwait(true);
            StatusMessage = "Installed packages and updates were refreshed.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The operation finished, but refresh failed: {ex.Message}";
        }
    }

    private void ApplyQueueSnapshot(OperationQueueSnapshot snapshot)
    {
        CurrentOperation = snapshot.Current;
        ReplaceAll(PendingOperations, snapshot.Pending);
        ReplaceAll(OperationHistory, snapshot.History);
        OnPropertyChanged(nameof(HasActiveOperation));
        OnPropertyChanged(nameof(ActivitySummary));
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
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _operationQueue.Changed -= OnOperationQueueChanged;
    }
}
