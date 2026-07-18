using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using PackagePilot.App.ViewModels;
using PackagePilot.App.Views;
using PackagePilot.App.Services;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using Windows.System;
using Windows.Storage;

namespace PackagePilot.App;

public sealed partial class MainPage : Page
{
    private DiscoverPage? _discoverPage;
    private InstalledPage? _installedPage;
    private UpdatesPage? _updatesPage;
    private ActivityPage? _activityPage;
    private SourcesPage? _sourcesPage;
    private SettingsPage? _settingsPage;
    private AppActivationRequest? _pendingActivation;
    private bool _initialized;
    private bool _activationReady;
    private bool _synchronizingNavigationSelection;
    private readonly IPrivilegedSourceManagementBroker? _sourceManagementBroker;
    private readonly IAppLifetimeController? _appLifetimeController;
    private readonly IAppLifetimeActivityGate _lifetimeActivityGate;
    private readonly IOperationDiagnosticsService? _operationDiagnosticsService;
    private readonly DestinationUiStateCoordinator _uiCoordinator;
    private readonly ShellActivityViewModel _shellActivityViewModel = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _activityStripTimer;
    private readonly HashSet<DestinationChangeFlags> _destinationsWithContent = [];
    private readonly DiscoverPageViewModel _discoverViewModel = new();
    private readonly InstalledPageViewModel _installedViewModel = new();
    private readonly UpdatesPageViewModel _updatesViewModel = new();
    private readonly ActivityPageViewModel _activityViewModel = new();
    private readonly SourcesPageViewModel _sourcesViewModel = new();
    private readonly SettingsPageViewModel _settingsViewModel = new();
    private long _navigationStartedAt;
    private string _navigationDestination = "discover";

    public MainPage(
        ShellViewModel viewModel,
        IPrivilegedSourceManagementBroker? sourceManagementBroker = null,
        IAppLifetimeController? appLifetimeController = null,
        IAppLifetimeActivityGate? lifetimeActivityGate = null,
        IOperationDiagnosticsService? operationDiagnosticsService = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _sourceManagementBroker = sourceManagementBroker;
        _appLifetimeController = appLifetimeController;
        _lifetimeActivityGate = lifetimeActivityGate ?? new AppLifetimeActivityGate();
        _operationDiagnosticsService = operationDiagnosticsService;
        InitializeComponent();
        _uiCoordinator = new DestinationUiStateCoordinator(
            DispatcherQueue,
            UpdateDestinationStates,
            ApplyUiChanges);
        _activityStripTimer = DispatcherQueue.CreateTimer();
        _activityStripTimer.IsRepeating = false;
        _activityStripTimer.Tick += (_, _) => UpdateShellChrome();

        ContentFrame.CacheSize = 8;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SubscribeToCollection(ViewModel.SearchResults, DestinationChangeFlags.Discover);
        SubscribeToCollection(
            ViewModel.InstalledPackages,
            DestinationChangeFlags.Discover | DestinationChangeFlags.Installed);
        SubscribeToCollection(
            ViewModel.InstalledApps,
            DestinationChangeFlags.Discover | DestinationChangeFlags.Installed);
        SubscribeToCollection(ViewModel.InstalledAppProviders, DestinationChangeFlags.Installed);
        SubscribeToCollection(
            ViewModel.AvailableUpdates,
            DestinationChangeFlags.Discover | DestinationChangeFlags.Updates | DestinationChangeFlags.Shell);
        SubscribeToCollection(
            ViewModel.SourceStatuses,
            DestinationChangeFlags.Discover | DestinationChangeFlags.Settings);
        SubscribeToCollection(
            ViewModel.PendingOperations,
            DestinationChangeFlags.Discover | DestinationChangeFlags.Updates |
            DestinationChangeFlags.Activity | DestinationChangeFlags.Shell);
        SubscribeToCollection(
            ViewModel.OperationHistory,
            DestinationChangeFlags.Discover | DestinationChangeFlags.Updates |
            DestinationChangeFlags.Activity | DestinationChangeFlags.Shell);

        AddNavigationAccelerator(VirtualKey.Number1, "discover");
        AddNavigationAccelerator(VirtualKey.Number2, "installed");
        AddNavigationAccelerator(VirtualKey.Number3, "updates");
        AddNavigationAccelerator(VirtualKey.Number4, "activity");
        AddNavigationAccelerator(VirtualKey.Number5, "sources");
        AddNavigationAccelerator(VirtualKey.Number6, "settings");
        AddGlobalAccelerator(VirtualKey.R, () => OnRefreshRequested(this, EventArgs.Empty));
        AddGlobalAccelerator(VirtualKey.F, FocusDiscoverSearch);
        UpdateDestinationStates(DestinationChangeFlags.All);
    }

    public ShellViewModel ViewModel { get; }
    internal event EventHandler<SettingChangedEventArgs>? AppSettingChanged;

    public Task ActivateAsync(AppActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_activationReady)
        {
            _pendingActivation = request;
            return Task.CompletedTask;
        }

        return ApplyActivationAsync(request);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ApplyStoredTheme();
        var initialActivation = _pendingActivation;
        _pendingActivation = null;
        NavigateTo(ToNavigationTag(initialActivation?.Destination ?? AppDestination.Discover));
        UpdateShellChrome();
        await ViewModel.InitializeAsync();
        _activationReady = true;
        await ApplyActivationAsync(_pendingActivation ?? initialActivation ?? new AppActivationRequest());
        _pendingActivation = null;
        UpdateShellChrome();
        _uiCoordinator.Invalidate(DestinationChangeFlags.All);
    }

    private async Task ApplyActivationAsync(AppActivationRequest request)
    {
        NavigateTo(ToNavigationTag(request.Destination));

        if (!string.IsNullOrWhiteSpace(request.SearchQuery))
        {
            ViewModel.SearchText = request.SearchQuery;
            _discoverPage?.SetSearchQuery(request.SearchQuery);
            await ViewModel.SearchAsync();
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
        }

        if (request.CheckForUpdates)
        {
            await ViewModel.RefreshUpdatesAsync();
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
        }
    }

    private static string ToNavigationTag(AppDestination destination) => destination switch
    {
        AppDestination.Installed => "installed",
        AppDestination.Updates => "updates",
        AppDestination.Activity => "activity",
        AppDestination.Settings => "settings",
        AppDestination.Sources => "sources",
        _ => "discover"
    };

    private DestinationChangeFlags GetDestinationFlag(object? content) => content switch
    {
        DiscoverPage => DestinationChangeFlags.Discover,
        InstalledPage => DestinationChangeFlags.Installed,
        UpdatesPage => DestinationChangeFlags.Updates,
        ActivityPage => DestinationChangeFlags.Activity,
        SourcesPage => DestinationChangeFlags.Sources,
        SettingsPage => DestinationChangeFlags.Settings,
        PackageDetailsPage => _uiCoordinator.ActiveDestination is DestinationChangeFlags.Discover
            or DestinationChangeFlags.Installed
                ? _uiCoordinator.ActiveDestination
                : DestinationChangeFlags.Discover,
        _ => DestinationChangeFlags.None
    };

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_synchronizingNavigationSelection)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            NavigateTo("settings");
            return;
        }

        if (args.SelectedItemContainer?.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        _navigationStartedAt = Stopwatch.GetTimestamp();
        _navigationDestination = tag;
        var pageType = tag switch
        {
            "installed" => typeof(InstalledPage),
            "updates" => typeof(UpdatesPage),
            "activity" => typeof(ActivityPage),
            "sources" => typeof(SourcesPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DiscoverPage)
        };

        SynchronizeNavigationSelection(tag);

        // CurrentSourcePageType can remain associated with the last destination while a
        // cached page or package-details page is the visible Content. The visible object is
        // the source of truth for shortcut navigation.
        if (ContentFrame.Content?.GetType() == pageType)
        {
            return;
        }

        var navigated = ReadBooleanSetting("reduceMotion", false)
            ? ContentFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo())
            : ContentFrame.Navigate(pageType);
        if (!navigated)
        {
            SynchronizeNavigationSelectionForContent(ContentFrame.Content);
        }
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void OnContentNavigated(object sender, NavigationEventArgs e)
    {
        PackagePilotUiEventSource.Log.DestinationActivated(
            _navigationDestination,
            _navigationStartedAt == 0
                ? 0
                : Stopwatch.GetElapsedTime(_navigationStartedAt).TotalMilliseconds);
        ShellNavigation.IsBackEnabled = ContentFrame.CanGoBack;
        SynchronizeNavigationSelectionForContent(e.Content);

        switch (e.Content)
        {
            case DiscoverPage page:
                if (!ReferenceEquals(_discoverPage, page))
                {
                    _discoverPage = page;
                    page.NavigationCacheMode = NavigationCacheMode.Required;
                    page.SearchRequested += OnSearchRequested;
                    page.RefreshRequested += OnRefreshRequested;
                    page.PackageActionRequested += OnPackageActionRequested;
                    page.PackageSelected += OnPackageSelected;
                }
                break;
            case InstalledPage page:
                if (!ReferenceEquals(_installedPage, page))
                {
                    _installedPage = page;
                    page.NavigationCacheMode = NavigationCacheMode.Required;
                    page.RefreshRequested += OnRefreshRequested;
                    page.PackageActionRequested += OnPackageActionRequested;
                    page.PackageSelected += OnPackageSelected;
                }
                break;
            case UpdatesPage page:
                if (!ReferenceEquals(_updatesPage, page))
                {
                    _updatesPage = page;
                    page.NavigationCacheMode = NavigationCacheMode.Required;
                    page.RefreshRequested += OnRefreshRequested;
                    page.PackageActionRequested += OnPackageActionRequested;
                    page.BulkUpdateRequested += OnBulkUpdateRequested;
                }
                break;
            case ActivityPage page:
                if (!ReferenceEquals(_activityPage, page))
                {
                    _activityPage = page;
                    page.NavigationCacheMode = NavigationCacheMode.Required;
                    page.CancelOperationRequested += OnCancelOperationRequested;
                    page.CancelQueuedRequested += OnCancelQueuedRequested;
                    page.ClearCompletedRequested += OnClearCompletedRequested;
                    page.ConfigureDiagnostics(
                        _operationDiagnosticsService,
                        ResolveDiagnosticOperation);
                }
                break;
            case SourcesPage page:
                if (!ReferenceEquals(_sourcesPage, page))
                {
                    _sourcesPage = page;
                    page.NavigationCacheMode = NavigationCacheMode.Required;
                    page.RefreshRequested += OnManagedSourcesRefreshRequested;
                    page.RefreshSourceRequested += OnSourceRefreshRequested;
                    page.AddRequested += OnSourceAddRequested;
                    page.RemoveRequested += OnSourceRemoveRequested;
                    page.ResetRequested += OnSourceResetRequested;
                    page.ToggleExplicitRequested += OnSourceToggleExplicitRequested;
                    _ = LoadManagedSourcesAsync();
                }
                break;
            case SettingsPage page:
                if (!ReferenceEquals(_settingsPage, page))
                {
                    _settingsPage = page;
                    page.NavigationCacheMode = NavigationCacheMode.Required;
                    page.ConfigureAppLifetime(
                        _appLifetimeController,
                        _lifetimeActivityGate);
                    page.SettingChanged += OnSettingChanged;
                }
                break;
            case PackageDetailsPage page:
                page.PackageActionRequested -= OnPackageActionRequested;
                page.PackageActionRequested += OnPackageActionRequested;
                break;
        }

        // Frame.Navigated is raised while the native XAML navigation transaction is
        // still unwinding. Installed inventory can contain hundreds of rows; realizing
        // those templates reentrantly from this callback has caused a native E_POINTER
        // crash. Let navigation and Loaded finish, then apply the current snapshot.
        var destination = GetDestinationFlag(ContentFrame.Content);
        if (destination != DestinationChangeFlags.None)
        {
            _uiCoordinator.Activate(destination);
        }
    }

    private async void OnSearchRequested(object? sender, SearchRequestedEventArgs e)
    {
        ViewModel.SearchText = e.Query;
        _discoverPage?.SetLoading(true);
        await ViewModel.SearchAsync();

        if (!string.Equals(ViewModel.SearchText.Trim(), e.Query.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        var sourcesRequiringConsent = ViewModel.SourceStatuses
            .Where(source => source.Agreements.Count > 0
                && !string.IsNullOrWhiteSpace(source.AgreementFingerprint))
            .ToArray();
        if (sourcesRequiringConsent.Length > 0)
        {
            var acceptedSources = new List<PackageSourceStatus>();
            foreach (var source in sourcesRequiringConsent)
            {
                if (!await ConfirmAgreementSetAsync(
                        $"Terms for {source.Name}",
                        "These exact source terms must be accepted before this source can return packages. A terms change will require consent again.",
                        source.Agreements,
                        "Accept and continue"))
                {
                    acceptedSources.Clear();
                    break;
                }

                acceptedSources.Add(source);
            }

            if (acceptedSources.Count == sourcesRequiringConsent.Length)
            {
                await ViewModel.SearchAcceptingSourceAgreementsAsync(acceptedSources);
            }
        }

        _discoverPage?.SetLoading(false);
        _uiCoordinator.Invalidate(DestinationChangeFlags.Discover | DestinationChangeFlags.Shell);
        ShowSearchDiagnostics();
    }

    private async void OnPackageSelected(object? sender, PackageActionRequestedEventArgs e)
    {
        var package = FindPackage(e.Package);
        if (package is null)
        {
            if (sender is InstalledPage installedPage && e.Package.InstalledAppId is not null)
            {
                installedPage.ShowPackageDetails(e.Package);
            }
            return;
        }

        await ViewModel.SelectPackageAsync(package);
        var details = ViewModel.SelectedDetails;
        if (sender is DiscoverPage discoverPage)
        {
            var item = ToDiscoverViewItem(
                package,
                CreateDiscoverStateIndex(),
                details);
            discoverPage.ShowPackageDetails(item);
        }
        else if (sender is InstalledPage installedPage)
        {
            var item = ToViewItem(package, e.Package.ActionLabel, details);
            installedPage.ShowPackageDetails(item);
        }
    }

    private async void OnRefreshRequested(object? sender, EventArgs e)
    {
        var refreshTarget = sender is DiscoverPage or InstalledPage or UpdatesPage
            ? sender
            : ContentFrame.Content;

        switch (refreshTarget)
        {
            case DiscoverPage:
                await ViewModel.RefreshDiscoverAsync();
                break;
            case InstalledPage:
                await ViewModel.RefreshInstalledAsync();
                break;
            case UpdatesPage:
                await ViewModel.RefreshUpdatesAsync();
                break;
            case SourcesPage:
                await ViewModel.RefreshManagedSourcesAsync();
                break;
            case SettingsPage:
                await ViewModel.RefreshSourcesAsync();
                break;
            default:
                var destination = (ShellNavigation.SelectedItem as NavigationViewItem)?.Tag as string;
                switch (destination)
                {
                    case "installed":
                        await ViewModel.RefreshInstalledAsync();
                        break;
                    case "updates":
                        await ViewModel.RefreshUpdatesAsync();
                        break;
                    case "settings":
                        await ViewModel.RefreshSourcesAsync();
                        break;
                    case "sources":
                        await ViewModel.RefreshManagedSourcesAsync();
                        break;
                    case "discover":
                        await ViewModel.RefreshDiscoverAsync();
                        break;
                    default:
                        ViewModel.SetStatusMessage("This page has no refreshable data.");
                        break;
                }
                break;
        }

        _uiCoordinator.Invalidate(DestinationChangeFlags.All);
    }

    private async Task LoadManagedSourcesAsync()
    {
        _sourcesPage?.SetLoading(true);
        await ViewModel.RefreshManagedSourcesAsync();
        _sourcesPage?.SetLoading(false);
        _uiCoordinator.Invalidate(DestinationChangeFlags.Sources | DestinationChangeFlags.Settings);
    }

    private async void OnManagedSourcesRefreshRequested(object? sender, EventArgs e) =>
        await LoadManagedSourcesAsync();

    private async void OnSourceRefreshRequested(object? sender, SourceCommandRequestedEventArgs e)
    {
        _sourcesPage?.SetLoading(true);
        var result = await ViewModel.RefreshManagedSourceAsync(e.Source.Name);
        await ViewModel.RefreshManagedSourcesAsync();
        _sourcesPage?.SetLoading(false);
        if (result is not null)
        {
            _sourcesPage?.ShowStatus(
                result.IsSuccess ? "Source refreshed" : "Source refresh failed",
                result.Message,
                result.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        _uiCoordinator.Invalidate(DestinationChangeFlags.Sources | DestinationChangeFlags.Settings);
    }

    private async void OnSourceAddRequested(object? sender, EventArgs e)
    {
        if (!CanRunSourceMutation(ViewModel.SourceManagementCapabilities.SupportsAdd))
        {
            return;
        }

        var nameBox = new TextBox
        {
            Header = "Name",
            PlaceholderText = "contoso"
        };
        var locationBox = new TextBox
        {
            Header = "HTTPS or UNC location",
            PlaceholderText = "https://packages.example.com/cache"
        };
        var typeBox = new ComboBox
        {
            Header = "Source type",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "PreIndexed", "REST" },
            SelectedIndex = 0
        };
        var explicitBox = new CheckBox
        {
            Content = "Use only when explicitly selected",
            IsChecked = true
        };
        var trustedBox = new CheckBox
        {
            Content = "Mark this source as trusted",
            IsChecked = false
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Custom sources are untrusted and excluded from ordinary discovery by default. Package Pilot never stores custom headers.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(nameBox);
        content.Children.Add(locationBox);
        content.Children.Add(typeBox);
        content.Children.Add(explicitBox);
        content.Children.Add(trustedBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add a WinGet source",
            Content = content,
            PrimaryButtonText = "Review and add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var request = new AddPackageSourceRequest
        {
            Name = nameBox.Text.Trim(),
            Location = locationBox.Text.Trim(),
            Type = typeBox.SelectedIndex == 1
                ? PackageSourceType.Rest
                : PackageSourceType.PreIndexed,
            IsExplicit = explicitBox.IsChecked == true,
            TrustLevel = trustedBox.IsChecked == true
                ? PackageSourceTrustLevel.Trusted
                : PackageSourceTrustLevel.None
        };
        var validation = SourceRequestValidator.Validate(request);
        if (!validation.IsValid)
        {
            _sourcesPage?.ShowStatus(
                "Source details need attention",
                string.Join(" ", validation.Errors),
                InfoBarSeverity.Warning);
            return;
        }

        await ExecuteSourceMutationAsync(
            PrivilegedSourceRequest.Add(request),
            "Source added");
    }

    private async void OnSourceRemoveRequested(
        object? sender,
        SourceCommandRequestedEventArgs e)
    {
        if (!CanRunSourceMutation(
                ViewModel.SourceManagementCapabilities.SupportsRemove
                && !IsPredefinedSource(e.Source.Id)))
        {
            return;
        }

        if (!await ConfirmSourceMutationAsync(
                $"Remove {e.Source.Name}?",
                "Package Pilot will ask Windows for administrator approval, remove only this named source, and then refresh the source list.",
                "Remove source"))
        {
            return;
        }

        await ExecuteSourceMutationAsync(
            PrivilegedSourceRequest.Remove(e.Source.Name),
            "Source removed");
    }

    private async void OnSourceResetRequested(
        object? sender,
        SourceCommandRequestedEventArgs e)
    {
        if (!CanRunSourceMutation(
                ViewModel.SourceManagementCapabilities.SupportsResetOne
                && IsPredefinedSource(e.Source.Id)))
        {
            return;
        }

        _sourcesPage?.SetLoading(true);
        var refresh = await ViewModel.RefreshManagedSourceAsync(e.Source.Name);
        _sourcesPage?.SetLoading(false);
        if (refresh is null)
        {
            _sourcesPage?.ShowStatus(
                "Repair stopped",
                "The named source could not be refreshed.",
                InfoBarSeverity.Warning);
            return;
        }

        var refreshSummary = refresh.IsSuccess
            ? $"The refresh of {e.Source.Name} completed, but the source still needs repair."
            : $"The refresh of {e.Source.Name} failed: {refresh.Message}";
        if (!await ConfirmSourceMutationAsync(
                $"Reset {e.Source.Name}?",
                $"{refreshSummary} Reset only this predefined source to its Windows default?",
                "Reset this source"))
        {
            return;
        }

        await ExecuteSourceMutationAsync(
            PrivilegedSourceRequest.Reset(e.Source.Name, isConfirmed: true),
            "Source repaired");
    }

    private async void OnSourceToggleExplicitRequested(
        object? sender,
        SourceCommandRequestedEventArgs e)
    {
        if (!CanRunSourceMutation(
                ViewModel.SourceManagementCapabilities.SupportsExplicitEdit
                && !IsPredefinedSource(e.Source.Id)))
        {
            return;
        }

        var makeExplicit = !e.Source.IsExplicit;
        var action = makeExplicit ? "Make explicit" : "Include in discovery";
        var explanation = makeExplicit
            ? "This source will be used only when it is selected explicitly."
            : "This source may participate in ordinary package discovery.";
        if (!await ConfirmSourceMutationAsync(
                $"{action}: {e.Source.Name}?",
                $"{explanation} Windows will request administrator approval.",
                action))
        {
            return;
        }

        await ExecuteSourceMutationAsync(
            PrivilegedSourceRequest.EditExplicit(e.Source.Name, makeExplicit),
            "Source updated");
    }

    private bool CanRunSourceMutation(bool capabilityAvailable)
    {
        if (_sourceManagementBroker is not null && capabilityAvailable)
        {
            return true;
        }

        _sourcesPage?.ShowStatus(
            "Source change unavailable",
            _sourceManagementBroker is null
                ? "The packaged source-administration helper is unavailable."
                : ViewModel.SourceManagementCapabilities.UnavailableReason
                  ?? "This version of WinGet or organization policy does not support that change.",
            InfoBarSeverity.Warning);
        return false;
    }

    private bool IsPredefinedSource(string sourceId) =>
        ViewModel.ManagedSources.FirstOrDefault(source => string.Equals(
            source.Id,
            sourceId,
            StringComparison.Ordinal))?.IsPredefined == true;

    private async Task<bool> ConfirmSourceMutationAsync(
        string title,
        string message,
        string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ExecuteSourceMutationAsync(
        PrivilegedSourceRequest request,
        string successTitle)
    {
        if (_sourceManagementBroker is null)
        {
            return;
        }

        var activity = _lifetimeActivityGate.TryEnter(
            AppLifetimeActivityKind.SourceMutation);
        if (activity is null)
        {
            _sourcesPage?.ShowStatus(
                "Source operation already running",
                "Wait for the current package-source operation to finish before starting another.",
                InfoBarSeverity.Informational);
            return;
        }

        _sourcesPage?.SetLoading(true);
        SourceOperationResult result;
        try
        {
            result = await _sourceManagementBroker.ExecuteElevatedAsync(request);
        }
        catch (OperationCanceledException)
        {
            _sourcesPage?.ShowStatus(
                "Source change canceled",
                "The source was not changed.",
                InfoBarSeverity.Informational);
            return;
        }
        finally
        {
            activity.Dispose();
            await ViewModel.RefreshManagedSourcesAsync();
            _sourcesPage?.SetLoading(false);
        }

        _sourcesPage?.ShowStatus(
            result.IsSuccess ? successTitle : "Source change failed",
            result.Message,
            result.IsSuccess ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        _uiCoordinator.Invalidate(DestinationChangeFlags.All);
    }

    private async void OnPackageActionRequested(object? sender, PackageActionRequestedEventArgs e)
    {
        if (e.Package.RequestedOperationKind is null
            && e.Package.InstalledActionKind is null
            && !string.IsNullOrWhiteSpace(e.Package.InstalledAppId))
        {
            NavigateTo("installed");
            _installedPage?.SelectPackageById(e.Package.InstalledAppId);
            return;
        }

        if (e.Package.InstalledActionKind is not null)
        {
            await HandleInstalledAppActionAsync(e.Package);
            return;
        }

        var package = FindPackage(e.Package);
        if (package is null)
        {
            ShowPageStatus("Package unavailable", "Refresh the page and try again.", InfoBarSeverity.Warning);
            return;
        }

        if (!ViewModel.CanQueuePackageMutations)
        {
            ShowMutationRecoveryUnavailable();
            return;
        }

        var retryAsAdministrator = e.Package.RequiresAdministratorRetry;
        if (retryAsAdministrator && !ViewModel.CanRetryPackageAsAdministrator)
        {
            ShowPageStatus(
                "Administrator retry unavailable",
                "The packaged one-shot administrator helper is unavailable. Reinstall or update Package Pilot before retrying this operation.",
                InfoBarSeverity.Warning);
            return;
        }

        var kind = e.Package.RequestedOperationKind ??
            (e.Package.ActionLabel switch
            {
                "Update" or "Retry" => PackageOperationKind.Upgrade,
                "Uninstall" => PackageOperationKind.Uninstall,
                _ => PackageOperationKind.Install
            });

        if (retryAsAdministrator
            && !ViewModel.CanRetryPackageAsAdministratorFor(package.Key, kind))
        {
            ShowPageStatus(
                "Administrator retry no longer applies",
                "The latest result for this exact package and source no longer requires an administrator retry. Refresh and review its current state before continuing.",
                InfoBarSeverity.Informational);
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
            return;
        }

        if (ViewModel.IsPackageMutationBlocked(package.Key))
        {
            ShowPageStatus(
                "Already queued",
                $"{package.Name} already has this operation queued or running. Follow it in Activity.",
                InfoBarSeverity.Informational);
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
            return;
        }

        if (kind == PackageOperationKind.Uninstall && !await ConfirmUninstallAsync(package))
        {
            return;
        }

        if (!retryAsAdministrator
            && kind != PackageOperationKind.Uninstall
            && !await ConfirmElevationAsync(package))
        {
            return;
        }

        var acceptance = kind == PackageOperationKind.Uninstall
            ? new AgreementAcceptance(true, false, null, false, null)
            : await ConfirmAgreementsAsync(
                package,
                requireExactAgreementSnapshot: retryAsAdministrator);

        if (!acceptance.Accepted)
        {
            return;
        }

        if (retryAsAdministrator
            && !await ConfirmAdministratorRetryAsync(package, kind))
        {
            return;
        }

        if (retryAsAdministrator
            && (!ViewModel.CanRetryPackageAsAdministratorFor(package.Key, kind)
                || ViewModel.IsPackageMutationBlocked(package.Key)))
        {
            ShowPageStatus(
                "Administrator retry state changed",
                "This package changed while you were reviewing the request. Refresh and review its latest state before authorizing an administrator retry.",
                InfoBarSeverity.Informational);
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
            return;
        }

        var (scope, architecture) = ReadInstallPreferences();
        try
        {
            ViewModel.EnqueueOperation(
                package,
                kind,
                acceptance.AcceptedSourceAgreements,
                acceptance.AcceptedPackageAgreements,
                scope,
                architecture,
                acceptance.AcceptedSourceAgreementFingerprint,
                acceptance.AcceptedPackageAgreementFingerprint,
                retryAsAdministrator);
        }
        catch (MutationRecoveryUnavailableException)
        {
            ShowMutationRecoveryUnavailable();
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
            return;
        }
        catch (DuplicatePackageOperationException)
        {
            ShowPageStatus(
                "Already queued",
                $"{package.Name} already has this operation queued or running. Follow it in Activity.",
                InfoBarSeverity.Informational);
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowPageStatus(
                "Could not queue package operation",
                exception.Message,
                InfoBarSeverity.Warning);
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
            return;
        }

        ViewModel.SetStatusMessage(retryAsAdministrator
            ? $"{package.Name} was queued for one administrator-approved retry."
            : $"{package.Name} was added to the operation queue.");
        if (kind == PackageOperationKind.Upgrade && sender is UpdatesPage)
        {
            _updatesPage?.ShowOperationStatus(
                retryAsAdministrator ? "Administrator retry queued" : "Update queued",
                retryAsAdministrator
                    ? $"{package.Name} is queued. Windows will show one UAC prompt when it reaches the front of the queue."
                    : $"{package.Name} is queued. Its row now shows progress, and full details remain available in Activity.",
                InfoBarSeverity.Informational);
        }
        UpdateShellChrome();
        _uiCoordinator.Invalidate(DestinationChangeFlags.All);
    }

    private async Task HandleInstalledAppActionAsync(PackageListItem item)
    {
        var app = ViewModel.InstalledApps.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, item.InstalledAppId, StringComparison.Ordinal));
        if (app is null || item.InstalledActionKind is not { } actionKind)
        {
            ShowPageStatus("Application unavailable", "Refresh Installed and try again.", InfoBarSeverity.Warning);
            return;
        }

        if (actionKind is InstalledAppActionKind.UninstallWithWinget
                or InstalledAppActionKind.RemoveMsix
            && !ViewModel.CanQueuePackageMutations)
        {
            ShowMutationRecoveryUnavailable();
            return;
        }

        switch (actionKind)
        {
            case InstalledAppActionKind.UninstallWithWinget:
                var wingetPackage = FindPackage(item);
                if (wingetPackage is null || !await ConfirmUninstallAsync(wingetPackage))
                {
                    return;
                }

                try
                {
                    ViewModel.EnqueueOperation(wingetPackage, PackageOperationKind.Uninstall);
                }
                catch (MutationRecoveryUnavailableException)
                {
                    ShowMutationRecoveryUnavailable();
                    return;
                }
                catch (DuplicatePackageOperationException)
                {
                    ShowPageStatus(
                        "Already queued",
                        $"{app.Name} already has this operation queued or running. Follow it in Activity.",
                        InfoBarSeverity.Informational);
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    ShowPageStatus(
                        "Could not queue uninstall",
                        exception.Message,
                        InfoBarSeverity.Warning);
                    return;
                }
                ViewModel.SetStatusMessage($"{app.Name} was added to the operation queue.");
                break;
            case InstalledAppActionKind.RemoveMsix:
                if (string.IsNullOrWhiteSpace(item.PackageFullName)
                    || !await ConfirmMsixRemovalAsync(app.Name))
                {
                    return;
                }

                try
                {
                    ViewModel.EnqueueMsixRemoval(app, item.PackageFullName);
                }
                catch (MutationRecoveryUnavailableException)
                {
                    ShowMutationRecoveryUnavailable();
                    return;
                }
                catch (DuplicatePackageOperationException)
                {
                    ShowPageStatus(
                        "Already queued",
                        $"{app.Name} already has this operation queued or running. Follow it in Activity.",
                        InfoBarSeverity.Informational);
                    return;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    ShowPageStatus(
                        "Could not queue uninstall",
                        exception.Message,
                        InfoBarSeverity.Warning);
                    return;
                }
                ViewModel.SetStatusMessage($"{app.Name} was added to the operation queue.");
                break;
            case InstalledAppActionKind.OpenStoreUpdates:
            case InstalledAppActionKind.OpenInstalledApps:
                if (item.ActionDestination is not null)
                {
                    await Launcher.LaunchUriAsync(item.ActionDestination);
                }
                break;
        }

        UpdateShellChrome();
        _uiCoordinator.Invalidate(DestinationChangeFlags.All);
    }

    private async Task<bool> ConfirmMsixRemovalAsync(string appName)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Uninstall {appName}?",
            Content = "Windows will remove this MSIX app for the current user. Once removal begins it cannot be cancelled from Package Pilot.",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnBulkUpdateRequested(object? sender, BulkPackageActionRequestedEventArgs e)
    {
        if (!ViewModel.CanQueuePackageMutations)
        {
            ShowMutationRecoveryUnavailable();
            return;
        }

        var queued = 0;
        var alreadyQueued = 0;
        var approved = new List<(PackageSummary Package, AgreementAcceptance Acceptance)>();
        foreach (var viewPackage in e.Packages)
        {
            var package = FindPackage(viewPackage);
            if (package is null)
            {
                continue;
            }

            if (ViewModel.IsPackageMutationBlocked(package.Key))
            {
                alreadyQueued++;
                continue;
            }

            var acceptance = await ConfirmAgreementsAsync(package);
            if (acceptance.Accepted)
            {
                approved.Add((package, acceptance));
            }
        }

        var (scope, architecture) = ReadInstallPreferences();
        try
        {
            var result = ViewModel.EnqueueOperations(
                approved.Select(entry => new PackageMutationRequest(
                    entry.Package,
                    PackageOperationKind.Upgrade,
                    entry.Acceptance.AcceptedSourceAgreements,
                    entry.Acceptance.AcceptedPackageAgreements,
                    scope,
                    architecture,
                    entry.Acceptance.AcceptedSourceAgreementFingerprint,
                    entry.Acceptance.AcceptedPackageAgreementFingerprint)),
                skipDuplicates: true);
            queued = result.OperationIds.Count;
            alreadyQueued += result.DuplicateCount;
        }
        catch (MutationRecoveryUnavailableException)
        {
            ShowMutationRecoveryUnavailable();
            return;
        }
        catch (DuplicatePackageOperationException)
        {
            // The batch API normally counts duplicates. Keep this conservative fallback
            // for an unexpected queue race between preflight and admission.
            alreadyQueued++;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _updatesPage?.ShowOperationStatus(
                "Could not queue updates",
                exception.Message,
                InfoBarSeverity.Warning);
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
            return;
        }

        ViewModel.SetStatusMessage(queued == 0
            ? alreadyQueued > 0
                ? "The selected updates are already queued or running."
                : "No updates were queued."
            : $"Queued {queued} update{(queued == 1 ? string.Empty : "s")}.");
        if (queued > 0 || alreadyQueued > 0)
        {
            _updatesPage?.ShowOperationStatus(
                queued > 0 ? "Updates queued" : "Already queued",
                queued > 0
                    ? $"Queued {queued} update{(queued == 1 ? string.Empty : "s")} for sequential installation. Track each row here or open Activity for details."
                    : "The selected updates are already queued or running. Track them here or in Activity.",
                InfoBarSeverity.Informational);
        }
        else
        {
            _updatesPage?.ShowStatus(
                "No updates queued",
                "No package changes were queued.",
                InfoBarSeverity.Warning);
        }
        UpdateShellChrome();
        _uiCoordinator.Invalidate(DestinationChangeFlags.All);
    }

    private void OnCancelOperationRequested(object? sender, OperationCancelRequestedEventArgs e)
    {
        if (!ViewModel.TryCancelOperation(e.Operation.OperationId))
        {
            ShowPageStatus(
                "Cancellation unavailable",
                "This installer has already started and now controls completion.",
                InfoBarSeverity.Warning);
        }
    }

    private void OnCancelQueuedRequested(object? sender, EventArgs e)
    {
        foreach (var entry in ViewModel.PendingOperations.Where(item => item.Progress.CanCancel).ToArray())
        {
            ViewModel.TryCancelOperation(entry.Operation.Id);
        }
    }

    private async void OnClearCompletedRequested(object? sender, EventArgs e)
    {
        if (ViewModel.PendingMutationVerificationCount > 0)
        {
            ViewModel.SetStatusMessage(
                "Activity was kept because one or more package operations still need verification.");
            return;
        }

        var diagnostics = ViewModel.OperationHistory
            .Select(result => result.EffectiveDiagnostic)
            .OfType<OperationDiagnosticReference>()
            .ToArray();
        if (!ViewModel.ClearHistory()
            || diagnostics.Length == 0
            || _operationDiagnosticsService is null)
        {
            return;
        }

        try
        {
            await _operationDiagnosticsService.DeleteOwnedLogsAsync(diagnostics);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ViewModel.SetStatusMessage(
                "Activity was cleared, but one or more app-owned installer logs could not be removed.");
        }
    }

    private bool TryFindDiagnosticOperation(
        Guid operationId,
        out OperationResult? completedOperation,
        out OperationQueueEntry? liveOperation)
    {
        completedOperation = ViewModel.OperationHistory.FirstOrDefault(result =>
            result.OperationId == operationId);
        if (completedOperation is not null)
        {
            liveOperation = null;
            return true;
        }

        var queue = ViewModel.OperationQueueSnapshot;
        liveOperation = queue.Current?.Operation.Id == operationId
            ? queue.Current
            : queue.Pending.FirstOrDefault(entry => entry.Operation.Id == operationId);
        return liveOperation is not null;
    }

    private OperationDiagnosticSelection? ResolveDiagnosticOperation(Guid operationId) =>
        TryFindDiagnosticOperation(operationId, out var completed, out var live)
            ? new OperationDiagnosticSelection(completed, live)
            : null;

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key == "theme" && e.Value is string theme)
        {
            RequestedTheme = theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        if (e.Key == "backgroundMonitoringState"
            && e.Value is BackgroundMonitoringState backgroundState)
        {
            ViewModel.SetBackgroundMonitoringState(backgroundState);
            _uiCoordinator.Invalidate(DestinationChangeFlags.All);
        }

        AppSettingChanged?.Invoke(this, e);
    }

    private void OnOpenActivityClick(object sender, RoutedEventArgs e)
    {
        _shellActivityViewModel.MarkReviewed(
            ViewModel.OperationQueueSnapshot,
            ViewModel.PendingMutationVerifications);
        NavigateTo("activity");
        UpdateShellChrome();
    }

    private async Task<bool> ConfirmUninstallAsync(PackageSummary package)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Uninstall {package.Name}?",
            Content = $"Package Pilot will ask WinGet to remove {package.Name}. The uninstaller may remove local application data and Windows may request administrator approval.",
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmElevationAsync(PackageSummary package)
    {
        if (package.ElevationRequirement is not (ElevationRequirement.Required or ElevationRequirement.MayRequire))
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Administrator approval may be required",
            Content = $"{package.Name} may ask Windows for administrator approval while its installer is running. Package Pilot itself will remain at normal integrity.",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmAdministratorRetryAsync(
        PackageSummary package,
        PackageOperationKind kind)
    {
        var action = kind switch
        {
            PackageOperationKind.Install => "install",
            PackageOperationKind.Upgrade => "update",
            PackageOperationKind.Uninstall => "uninstall",
            _ => "change"
        };
        var source = string.IsNullOrWhiteSpace(package.Key.SourceId)
            ? "(missing source)"
            : package.Key.SourceId;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Retry {package.Name} as administrator?",
            Content =
                $"When this operation reaches the front of the queue, Package Pilot will ask Windows for one UAC approval. A short-lived helper will then {action} only this exact WinGet package:{Environment.NewLine}{Environment.NewLine}" +
                $"Package: {package.Key.Id}{Environment.NewLine}Source: {source}{Environment.NewLine}{Environment.NewLine}" +
                "The main app remains at normal integrity. Approve with the current Windows account; alternate administrator credentials are rejected so Package Pilot cannot change another user's package profile. You can cancel the queued request in Activity until it reaches the front. After Windows begins the UAC request, Package Pilot cannot safely cancel it; you can decline the UAC prompt instead. The helper exits after returning one result.",
            PrimaryButtonText = "Queue administrator retry",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<AgreementAcceptance> ConfirmAgreementsAsync(
        PackageSummary package,
        bool requireExactAgreementSnapshot = false)
    {
        await ViewModel.SelectPackageAsync(package);
        if (requireExactAgreementSnapshot
            && ViewModel.SelectedDetails?.Summary.Key != package.Key)
        {
            ShowPageStatus(
                "Could not verify current agreements",
                "Package Pilot will not send this operation to the administrator helper until WinGet returns current details for the exact package and source.",
                InfoBarSeverity.Warning);
            return new AgreementAcceptance(false, false, null, false, null);
        }

        var sourceAgreements = (ViewModel.SelectedDetails?.Agreements ?? [])
            .Where(item => item.Kind == AgreementKind.Source)
            .ToArray();
        var currentSourceSnapshot = sourceAgreements.Length > 0
            ? SourceAgreementSnapshot.Create(package.Key.SourceId, sourceAgreements)
            : null;
        var acceptedSourceFingerprint =
            ViewModel.GetAcceptedSourceAgreementFingerprint(package.Key.SourceId);
        var acceptedSource = currentSourceSnapshot is null
            || currentSourceSnapshot.Matches(acceptedSourceFingerprint);
        if (!acceptedSource)
        {
            acceptedSource = await ConfirmAgreementSetAsync(
                $"Source agreements for {package.Name}",
                "Review the source terms before Package Pilot reconnects to retrieve package metadata.",
                sourceAgreements,
                "Accept source terms");
            if (!acceptedSource)
            {
                return new AgreementAcceptance(false, false, null, false, null);
            }

            acceptedSourceFingerprint = currentSourceSnapshot!.Fingerprint;
            ViewModel.AcceptPackageSourceAgreement(package, sourceAgreements);
        }

        if (currentSourceSnapshot is not null)
        {
            // AgreementRequired details contain only source terms. Even when persisted consent
            // exactly matches those current terms, reconnect before reviewing package terms.
            await ViewModel.SelectPackageAcceptingSourceAgreementsAsync(package);
            if (requireExactAgreementSnapshot
                && ViewModel.SelectedDetails?.Summary.Key != package.Key)
            {
                ShowPageStatus(
                    "Could not verify current agreements",
                    "The exact package details were unavailable after source-term review, so the administrator retry was not queued.",
                    InfoBarSeverity.Warning);
                return new AgreementAcceptance(false, false, null, false, null);
            }

            if (requireExactAgreementSnapshot
                && (ViewModel.SelectedDetails?.Agreements ?? [])
                    .Any(item => item.Kind == AgreementKind.Source))
            {
                ShowPageStatus(
                    "Source agreements changed",
                    "The source terms changed while the administrator retry was being reviewed. Review the updated terms in a new retry instead of authorizing stale consent.",
                    InfoBarSeverity.Warning);
                return new AgreementAcceptance(false, false, null, false, null);
            }
        }

        var packageAgreements = (ViewModel.SelectedDetails?.Agreements ?? [])
            .Where(item => item.Kind == AgreementKind.Package)
            .ToArray();
        var acceptedPackage = packageAgreements.Length == 0
            || await ConfirmAgreementSetAsync(
                $"Package agreements for {package.Name}",
                "Review the package terms supplied by WinGet before continuing.",
                packageAgreements,
                "Accept package terms");
        var acceptedPackageFingerprint =
            acceptedPackage && packageAgreements.Length > 0
                ? PackageAgreementSnapshot.Create(
                    package.Key,
                    packageAgreements).Fingerprint
                : null;

        return new AgreementAcceptance(
            acceptedPackage,
            acceptedSourceFingerprint is not null,
            acceptedSourceFingerprint,
            acceptedPackage && packageAgreements.Length > 0,
            acceptedPackageFingerprint);
    }

    private async Task<bool> ConfirmAgreementSetAsync(
        string title,
        string introduction,
        IReadOnlyList<PackageAgreement> agreements,
        string primaryButtonText)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = introduction,
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var agreement in agreements)
        {
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(agreement.Label) ? "Agreement" : agreement.Label,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(agreement.Text))
            {
                content.Children.Add(new TextBlock
                {
                    Text = agreement.Text,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            if (agreement.AgreementUri is not null)
            {
                content.Children.Add(new HyperlinkButton
                {
                    Content = "Open agreement",
                    NavigateUri = agreement.AgreementUri,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(0, 4, 0, 4)
                });
            }
        }

        var acceptanceBox = new CheckBox
        {
            Content = "I have reviewed and accept these terms",
            IsChecked = false
        };
        content.Children.Add(acceptanceBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer { MaxHeight = 480, Content = content },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        acceptanceBox.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        acceptanceBox.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private PackageSummary? FindPackage(PackageListItem item)
    {
        if (item.WingetPackage is { } wingetKey)
        {
            return ViewModel.SearchResults
                .Concat(ViewModel.InstalledPackages)
                .Concat(ViewModel.AvailableUpdates)
                .Concat(ViewModel.PendingUpgradeVerifications)
                .FirstOrDefault(package => package.Key == wingetKey);
        }

        return ViewModel.SearchResults
            .Concat(ViewModel.InstalledPackages)
            .Concat(ViewModel.AvailableUpdates)
            .Concat(ViewModel.PendingUpgradeVerifications)
            .FirstOrDefault(package =>
                string.Equals(package.Key.Id, item.PackageId, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(item.Source) ||
                 string.Equals(package.Key.SourceId, item.Source, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(package.SourceName, item.Source, StringComparison.OrdinalIgnoreCase)));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var flags = GetPropertyChangeFlags(e.PropertyName);
        _uiCoordinator.Invalidate(flags);
    }

    private void SubscribeToCollection(
        INotifyCollectionChanged collection,
        DestinationChangeFlags flags)
    {
        collection.CollectionChanged += (_, _) => _uiCoordinator.Invalidate(flags);
    }

    private void UpdateDestinationStates(DestinationChangeFlags changes)
    {
        if ((changes & DestinationChangeFlags.Discover) != 0)
        {
            _discoverViewModel.Apply(
                new DiscoverPageState(
                    ViewModel.SearchResults.ToArray(),
                    ViewModel.IsSearching,
                    ViewModel.CanQueuePackageMutations),
                changes);
        }

        if ((changes & DestinationChangeFlags.Installed) != 0)
        {
            _installedViewModel.Apply(
                new InstalledPageState(
                    ViewModel.InstalledApps.ToArray(),
                    ViewModel.InstalledPackages.ToArray(),
                    ViewModel.InstalledAppProviders.ToArray(),
                    ViewModel.IsInstalledInventoryAuthoritative,
                    ViewModel.CanQueuePackageMutations),
                changes);
        }

        if ((changes & DestinationChangeFlags.Updates) != 0)
        {
            _updatesViewModel.Apply(
                new UpdatesPageState(
                    ViewModel.AvailableUpdates.ToArray(),
                    ViewModel.PendingUpgradeVerifications.ToArray(),
                    ViewModel.UpdatesCheckState,
                    ViewModel.LastUpdateCheckAt,
                    ViewModel.LastSuccessfulUpdateCheckAt,
                    ViewModel.UpdateCheckError),
                changes);
        }

        if ((changes & DestinationChangeFlags.Activity) != 0)
        {
            _activityViewModel.Apply(
                new ActivityDestinationState(
                    ViewModel.OperationQueueSnapshot,
                    ViewModel.PendingMutationVerifications.ToArray()),
                changes);
        }

        if ((changes & DestinationChangeFlags.Sources) != 0)
        {
            _sourcesViewModel.Apply(
                new SourcesPageState(
                    ViewModel.ManagedSources.ToArray(),
                    ViewModel.SourceManagementCapabilities,
                    ViewModel.IsManagingSources),
                changes);
        }

        if ((changes & DestinationChangeFlags.Settings) != 0)
        {
            _settingsViewModel.Apply(
                new SettingsPageState(
                    ViewModel.WindowsIntegrationCapabilities,
                    ViewModel.SourceStatuses.ToArray(),
                    ViewModel.WindowsCapabilitySummary),
                changes);
        }
    }

    private DestinationChangeFlags GetPropertyChangeFlags(string? propertyName) => propertyName switch
    {
        nameof(ShellViewModel.SearchText)
            or nameof(ShellViewModel.IsSearching)
            or nameof(ShellViewModel.IsSearchTruncated)
            or nameof(ShellViewModel.IsDetailsLoading)
            => DestinationChangeFlags.Discover | DestinationChangeFlags.Shell,
        nameof(ShellViewModel.UpdatesCheckState)
            or nameof(ShellViewModel.IsCheckingForUpdates)
            or nameof(ShellViewModel.LastUpdateCheckAt)
            or nameof(ShellViewModel.LastSuccessfulUpdateCheckAt)
            or nameof(ShellViewModel.UpdateCheckError)
            => DestinationChangeFlags.Discover | DestinationChangeFlags.Updates |
               DestinationChangeFlags.Shell,
        nameof(ShellViewModel.SourceManagementCapabilities)
            or nameof(ShellViewModel.IsManagingSources)
            => DestinationChangeFlags.Sources | DestinationChangeFlags.Settings |
               DestinationChangeFlags.Shell,
        nameof(ShellViewModel.WindowsIntegrationCapabilities)
            or nameof(ShellViewModel.WindowsCapabilitySummary)
            => DestinationChangeFlags.Settings | DestinationChangeFlags.Shell,
        nameof(ShellViewModel.CurrentOperation)
            or nameof(ShellViewModel.HasActiveOperation)
            or nameof(ShellViewModel.ActivitySummary)
            or nameof(ShellViewModel.CanQueuePackageMutations)
            or nameof(ShellViewModel.CanRetryPackageAsAdministrator)
            or nameof(ShellViewModel.MutationVerificationPersistenceError)
            => DestinationChangeFlags.Discover | DestinationChangeFlags.Installed |
               DestinationChangeFlags.Updates | DestinationChangeFlags.Activity |
               DestinationChangeFlags.Shell,
        nameof(ShellViewModel.SelectedPackage)
            or nameof(ShellViewModel.SelectedDetails)
            or nameof(ShellViewModel.SelectedPackageHasAgreements)
            => DestinationChangeFlags.Discover,
        nameof(ShellViewModel.Capabilities)
            or nameof(ShellViewModel.IsInstalledInventoryAuthoritative)
            or nameof(ShellViewModel.IsReady)
            or nameof(ShellViewModel.HealthTitle)
            or nameof(ShellViewModel.HealthMessage)
            or nameof(ShellViewModel.IsBusy)
            or nameof(ShellViewModel.StatusMessage)
            => DestinationChangeFlags.All,
        _ => _uiCoordinator.ActiveDestination | DestinationChangeFlags.Shell
    };

    private void UpdateShellChrome()
    {
        var activity = _shellActivityViewModel.Project(
            ViewModel.OperationQueueSnapshot,
            ViewModel.PendingMutationVerifications,
            DateTimeOffset.UtcNow);
        ActivityStrip.Visibility = activity.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        ActivitySummaryText.Text = activity.Summary;
        StatusText.Text = activity.Detail;
        ActivityProgress.Visibility = activity.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        ActivityProgress.IsIndeterminate = activity.IsIndeterminate;
        ActivityProgress.Value = activity.Progress;
        (ActivityStateIcon.Glyph, ActivityStateIcon.Foreground) = activity.Severity switch
        {
            ShellActivitySeverity.Success =>
                ("\uE73E", (Brush)Application.Current.Resources["PackagePilotSuccessBrush"]),
            ShellActivitySeverity.Warning =>
                ("\uE7BA", (Brush)Application.Current.Resources["PackagePilotWarningBrush"]),
            ShellActivitySeverity.Error =>
                ("\uEA39", (Brush)Application.Current.Resources["PackagePilotCriticalBrush"]),
            _ => ("\uE895", (Brush)Application.Current.Resources["PackagePilotTextPrimaryBrush"])
        };
        _activityStripTimer.Stop();
        if (activity.RefreshAt is { } refreshAt)
        {
            _activityStripTimer.Interval = refreshAt > DateTimeOffset.UtcNow
                ? refreshAt - DateTimeOffset.UtcNow
                : TimeSpan.FromMilliseconds(1);
            _activityStripTimer.Start();
        }
        var visibleUpdateCount = ViewModel.AvailableUpdates
            .Concat(ViewModel.PendingUpgradeVerifications)
            .Select(package => package.Key)
            .Distinct()
            .Count();
        UpdatesBadge.Value = visibleUpdateCount > 0 ? visibleUpdateCount : -1;

        var mutationRecoveryUnavailable =
            ViewModel.IsReady && !ViewModel.CanQueuePackageMutations;
        HealthBanner.IsOpen = _initialized
            && (mutationRecoveryUnavailable || (!ViewModel.IsReady && !ViewModel.IsBusy));
        HealthBanner.Title = mutationRecoveryUnavailable
            ? "Package operations paused"
            : ViewModel.HealthTitle;
        HealthBanner.Message = mutationRecoveryUnavailable
            ? $"Package changes are disabled to protect operation recovery state. {ViewModel.MutationVerificationPersistenceError}"
            : ViewModel.HealthMessage;
    }

    private void ApplyUiChanges(DestinationChangeFlags changes)
    {
        if ((changes & DestinationChangeFlags.Discover) != 0 && _discoverPage is not null)
        {
            var discoverState = CreateDiscoverStateIndex();
            _discoverPage.SetResults(ViewModel.SearchResults.Select(item =>
                ToDiscoverViewItem(item, discoverState)));
            _discoverPage.SetLoading(ViewModel.IsSearching);
            _discoverPage.SetMutationActionsAvailable(ViewModel.CanQueuePackageMutations);
            MarkFirstContent(DestinationChangeFlags.Discover, ViewModel.SearchResults.Count);
        }

        if ((changes & DestinationChangeFlags.Installed) != 0 && _installedPage is not null)
        {
            _installedPage.SetPackages(
                ViewModel.InstalledAppProviders.Count > 0 || ViewModel.InstalledApps.Count > 0
                    ? ViewModel.InstalledApps.Select(ToViewItem)
                    : ViewModel.InstalledPackages.Select(item => ToViewItem(item, "Uninstall")));
            _installedPage.SetMutationActionsAvailable(ViewModel.CanQueuePackageMutations);
            MarkFirstContent(DestinationChangeFlags.Installed, ViewModel.InstalledApps.Count);
        }

        if ((changes & (DestinationChangeFlags.Discover | DestinationChangeFlags.Installed)) != 0
            && ContentFrame.Content is PackageDetailsPage detailsPage)
        {
            detailsPage.SetMutationActionsAvailable(ViewModel.CanQueuePackageMutations);
        }

        if ((changes & DestinationChangeFlags.Updates) != 0 && _updatesPage is not null)
        {
            var queue = ViewModel.OperationQueueSnapshot;
            var visibleUpdates = ViewModel.AvailableUpdates
                .Concat(ViewModel.PendingUpgradeVerifications)
                .GroupBy(package => package.Key)
                .Select(group => group.First());
            _updatesPage.SetUpdates(visibleUpdates.Select(item =>
                UpdateRowProjector.Apply(
                    ToViewItem(item, "Update"),
                    queue,
                    ViewModel.LastSuccessfulUpdateCheckAt,
                    ViewModel.IsMutationVerificationPending(item.Key),
                    ViewModel.IsRestartRequiredThisBoot(item.Key),
                    ViewModel.CanQueuePackageMutations,
                    ViewModel.GetMutationVerificationPhase(item.Key),
                    ViewModel.CanRetryPackageAsAdministrator)));
            _updatesPage.SetCheckState(
                ViewModel.UpdatesCheckState,
                ViewModel.LastUpdateCheckAt,
                ViewModel.UpdateCheckError);
            MarkFirstContent(DestinationChangeFlags.Updates, ViewModel.AvailableUpdates.Count);
        }

        if ((changes & DestinationChangeFlags.Activity) != 0 && _activityPage is not null)
        {
            var operations = new List<OperationListItem>();
            if (ViewModel.CurrentOperation is { } current)
            {
                operations.Add(OperationRowProjector.FromEntry(current));
            }
            operations.AddRange(ViewModel.PendingOperations.Select(OperationRowProjector.FromEntry));
            operations.AddRange(ViewModel.OperationHistory.Select(result =>
                OperationRowProjector.FromResult(
                    result,
                    ViewModel.GetMutationVerificationPhase(result))));
            var retainedOperationIds = ViewModel.OperationHistory
                .Select(result => result.OperationId)
                .ToHashSet();
            operations.AddRange(ViewModel.PendingMutationVerifications
                .Where(marker => !retainedOperationIds.Contains(marker.OperationId))
                .Select(OperationRowProjector.FromVerificationMarker));
            _activityPage.SetOperations(operations);
            MarkFirstContent(DestinationChangeFlags.Activity, operations.Count);
        }

        if ((changes & DestinationChangeFlags.Settings) != 0 && _settingsPage is not null)
        {
            _settingsPage.SetCapabilitySummary(ViewModel.WindowsCapabilitySummary);
            ReplaceAll(_settingsPage.Sources, ViewModel.SourceStatuses.Select(source => new SourceHealthItem
            {
                Name = source.Name,
                Identifier = source.Id,
                Status = source.Health.ToString(),
                Detail = source.Message ?? (source.Health == SourceHealth.Healthy ? "Available" : "No diagnostic detail")
            }));
        }


        if ((changes & DestinationChangeFlags.Sources) != 0 && _sourcesPage is not null)
        {
            var capabilities = ViewModel.SourceManagementCapabilities;
            _sourcesPage.SetCapabilitySummary(capabilities.IsAvailable
                ? $"WinGet source contract {capabilities.ContractVersion}. Changes request administrator approval."
                : capabilities.UnavailableReason ?? "Source management is unavailable.");
            _sourcesPage.SetLoading(
                ViewModel.IsManagingSources
                || _lifetimeActivityGate.Snapshot.HasSourceActivity);
            _sourcesPage.SetCanAdd(capabilities.SupportsAdd);
            ReplaceAll(_sourcesPage.Sources, ViewModel.ManagedSources.Select(source => new SourceManagementListItem
            {
                Id = source.Id,
                Name = source.Name,
                Type = source.TypeName,
                Location = source.Argument,
                Origin = source.Origin.ToString(),
                Trust = source.TrustLevel.ToString(),
                Status = source.Health.ToString(),
                LastUpdated = source.LastUpdatedAt is { } updated
                    ? $"Last updated {updated.ToLocalTime():g}"
                    : "Never updated",
                AgreementSummary = source.AgreementSnapshot.HasAgreements
                    ? $"{source.AgreementSnapshot.Agreements.Count} agreement{(source.AgreementSnapshot.Agreements.Count == 1 ? string.Empty : "s")} require exact-term consent"
                    : "No source agreements",
                IsExplicit = source.IsExplicit,
                CanRefresh = capabilities.SupportsRefresh,
                CanRemove = capabilities.SupportsRemove && !source.IsPredefined,
                CanReset = capabilities.SupportsResetOne && source.IsPredefined,
                CanEditExplicit = capabilities.SupportsExplicitEdit && !source.IsPredefined
            }));
        }

        if ((changes & DestinationChangeFlags.Shell) != 0)
        {
            UpdateShellChrome();
            if (ViewModel.CurrentOperation is { } current)
            {
                PackagePilotUiEventSource.Log.OperationProgressPresented(
                    current.Operation.Id.ToString("D"),
                    current.Progress.State.ToString());
            }
        }
    }

    private void MarkFirstContent(DestinationChangeFlags destination, int count)
    {
        if (_destinationsWithContent.Add(destination))
        {
            PackagePilotUiEventSource.Log.FirstContent(destination.ToString(), count);
        }
    }

    private DiscoverPackageStateIndex CreateDiscoverStateIndex() =>
        DiscoverRowProjector.CreateIndex(
            ViewModel.SearchResults,
            ViewModel.InstalledPackages,
            ViewModel.AvailableUpdates,
            ViewModel.OperationQueueSnapshot,
            ViewModel.InstalledApps);

    private PackageListItem ToDiscoverViewItem(
        PackageSummary package,
        DiscoverPackageStateIndex index,
        PackageDetails? details = null) =>
        DiscoverRowProjector.Apply(
            ToViewItem(package, details: details),
            package,
            index,
            ViewModel.CanQueuePackageMutations,
            ViewModel.IsMutationVerificationPending(package.Key),
            ViewModel.IsRestartRequiredThisBoot(package.Key),
            ViewModel.GetMutationVerificationPhase(package.Key),
            ViewModel.CanRetryPackageAsAdministrator);

    private PackageListItem ToViewItem(
        PackageSummary package,
        string? actionOverride = null,
        PackageDetails? details = null)
    {
        var action = actionOverride ?? package.Status switch
        {
            PackageStatus.UpdateAvailable => "Update",
            PackageStatus.Installed => "Uninstall",
            _ => "Install"
        };

        return new PackageListItem
        {
            Name = package.Name,
            PackageId = package.Key.Id,
            Source = string.IsNullOrWhiteSpace(package.SourceName) ? package.Key.SourceId : package.SourceName,
            InstalledVersion = package.InstalledVersion ?? string.Empty,
            AvailableVersion = package.AvailableVersion ?? string.Empty,
            Status = package.Status.ToString(),
            StateGlyph = package.Status switch
            {
                PackageStatus.UpdateAvailable => "\uE896",
                PackageStatus.Installed => "\uE73E",
                _ => string.Empty
            },
            IsPositiveState = package.Status == PackageStatus.Installed,
            ActionLabel = action,
            IsActionEnabled = ViewModel.CanQueuePackageMutations,
            RequestedOperationKind = action switch
            {
                "Update" => PackageOperationKind.Upgrade,
                "Uninstall" => PackageOperationKind.Uninstall,
                _ => PackageOperationKind.Install
            },
            WingetPackage = package.Key,
            Description = FirstNonEmpty(details?.Description, package.Description, "No description was supplied by this source."),
            IconUri = package.IconUri,
            Publisher = FirstNonEmpty(details?.Publisher, package.Publisher),
            License = FirstNonEmpty(details?.License, "Not provided"),
            Tags = details?.Tags.Count > 0 ? string.Join(", ", details.Tags) : "Not provided",
            Versions = details?.Versions.Count > 0 ? string.Join(", ", details.Versions) : "Not provided",
            Architecture = details is null ? "Auto" : FormatArchitecture(details.Architecture),
            Scope = details is null ? "Installer default" : FormatScope(details.InstallerScope),
            RequiresElevation = (details?.ElevationRequirement ?? package.ElevationRequirement) is ElevationRequirement.Required or ElevationRequirement.MayRequire,
            HomepageUri = details?.HomepageUri,
            PublisherUri = details?.PublisherUri,
            SupportUri = details?.SupportUri,
            LicenseUri = details?.LicenseUri,
            ReleaseNotes = details?.ReleaseNotes ?? string.Empty,
            ReleaseNotesUri = details?.ReleaseNotesUri
        };
    }

    private PackageListItem ToViewItem(InstalledApp app)
    {
        var inventoryIsAuthoritative = ViewModel.IsInstalledInventoryAuthoritative;
        var action = inventoryIsAuthoritative ? app.PrimaryAction : null;
        var providers = app.Installations
            .Select(installation => installation.Provider switch
            {
                InstalledAppProviderKind.Winget => "WinGet",
                InstalledAppProviderKind.Msix => installation.IsStoreApp ? "Microsoft Store" : "MSIX",
                InstalledAppProviderKind.Registry => "Windows registry",
                _ => installation.ProviderId
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var scopes = app.Installations
            .Select(installation => FormatScope(installation.Scope))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var architectures = app.Installations
            .Select(installation => FormatArchitecture(installation.Architecture))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return new PackageListItem
        {
            Name = app.Name,
            Publisher = app.Publisher,
            IconUri = app.Icon is
                {
                    Kind: AppIconSourceKind.BoundedHttpsMetadata,
                    Uri.Scheme: "https"
                } icon
                    ? icon.Uri
                    : null,
            IconReference = app.Icon,
            PackageId = app.Id,
            InstalledAppId = app.Id,
            Source = string.Join(", ", providers),
            InstalledVersion = app.VersionDisplay,
            Status = inventoryIsAuthoritative
                ? app.HasMultipleVersions ? "Multiple versions" : "Installed"
                : "Cached — confirming",
            StateGlyph = inventoryIsAuthoritative ? "\uE73E" : "\uE895",
            IsPositiveState = inventoryIsAuthoritative,
            ActionLabel = inventoryIsAuthoritative
                ? action?.Label ?? "Managed by Windows"
                : "Refreshing…",
            IsActionEnabled = inventoryIsAuthoritative && action is not null
                && (action.Kind is not (
                        InstalledAppActionKind.UninstallWithWinget
                        or InstalledAppActionKind.RemoveMsix)
                    || ViewModel.CanQueuePackageMutations),
            InstalledActionKind = action?.Kind,
            WingetPackage = action?.WingetPackage,
            PackageFullName = action?.PackageFullName,
            ActionDestination = action?.Destination,
            Description = app.Installations.Count == 1
                ? "One installation was detected."
                : $"{app.Installations.Count} installations were detected and kept as separate records.",
            Architecture = string.Join(", ", architectures),
            Scope = string.Join(", ", scopes),
            Versions = string.Join(", ", app.Installations
                .Select(installation => installation.Version)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Distinct(StringComparer.OrdinalIgnoreCase))
        };
    }

    private static void ReplaceAll<T>(ICollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private void ShowSearchDiagnostics()
    {
        if (_discoverPage is null)
        {
            return;
        }

        if (ViewModel.IsSearchTruncated)
        {
            _discoverPage.ShowStatus(
                "More than 100 matches",
                "Package Pilot shows the first 100 results. Refine your search to narrow the list.",
                InfoBarSeverity.Informational);
            return;
        }

        var unavailable = ViewModel.SourceStatuses
            .Where(source => source.Health != SourceHealth.Healthy)
            .ToArray();
        if (unavailable.Length > 0)
        {
            var names = string.Join(", ", unavailable.Select(source => source.Name));
            _discoverPage.ShowStatus(
                "Some sources are unavailable",
                $"Results from healthy sources are still shown. Check Settings for details about: {names}.",
                InfoBarSeverity.Warning);
        }
    }

    private void ApplyStoredTheme()
    {
        RequestedTheme = ReadStringSetting("theme", "system") switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private static (InstallerScope Scope, PackageArchitecture Architecture) ReadInstallPreferences()
    {
        var scope = ReadStringSetting("installScope", "default") switch
        {
            "user" => InstallerScope.User,
            "machine" => InstallerScope.Machine,
            _ => InstallerScope.Unknown
        };
        var architecture = ReadStringSetting("architecture", "auto") switch
        {
            "x64" => PackageArchitecture.X64,
            "x86" => PackageArchitecture.X86,
            _ => PackageArchitecture.Unknown
        };
        return (scope, architecture);
    }

    private static string ReadStringSetting(string key, string fallback)
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        return values.TryGetValue(key, out var value) && value is string text ? text : fallback;
    }

    private static bool ReadBooleanSetting(string key, bool fallback)
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        return values.TryGetValue(key, out var value) && value is bool flag ? flag : fallback;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatArchitecture(PackageArchitecture architecture) => architecture switch
    {
        PackageArchitecture.X64 => "x64",
        PackageArchitecture.X86 => "x86",
        PackageArchitecture.Arm64 => "ARM64",
        PackageArchitecture.Arm => "ARM",
        PackageArchitecture.Neutral => "Architecture neutral",
        _ => "Automatic"
    };

    private static string FormatScope(InstallerScope scope) => scope switch
    {
        InstallerScope.User => "Current user",
        InstallerScope.Machine => "All users",
        _ => "Installer default"
    };

    private void ShowMutationRecoveryUnavailable()
    {
        ViewModel.SetStatusMessage(
            "Package operations are paused because recovery state is unavailable.");
        ShowPageStatus(
            "Package operations paused",
            "Package Pilot cannot safely preserve package-operation recovery state. Restart the app after checking local storage access.",
            InfoBarSeverity.Warning);
    }

    private void ShowPageStatus(string title, string message, InfoBarSeverity severity)
    {
        switch (ContentFrame.Content)
        {
            case DiscoverPage page:
                page.ShowStatus(title, message, severity);
                break;
            case InstalledPage page:
                page.ShowStatus(title, message, severity);
                break;
            case UpdatesPage page:
                page.ShowStatus(title, message, severity);
                break;
            case SourcesPage page:
                page.ShowStatus(title, message, severity);
                break;
        }
    }

    private void AddNavigationAccelerator(VirtualKey key, string destination)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = VirtualKeyModifiers.Control
        };
        accelerator.Invoked += (_, args) =>
        {
            NavigateTo(destination);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void SynchronizeNavigationSelection(string destination)
    {
        object? selectedItem = destination == "settings"
            ? ShellNavigation.SettingsItem
            : ShellNavigation.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    destination,
                    StringComparison.Ordinal));

        if (selectedItem is null || ReferenceEquals(ShellNavigation.SelectedItem, selectedItem))
        {
            return;
        }

        _synchronizingNavigationSelection = true;
        try
        {
            ShellNavigation.SelectedItem = selectedItem;
        }
        finally
        {
            _synchronizingNavigationSelection = false;
        }
    }

    private void SynchronizeNavigationSelectionForContent(object? content)
    {
        var destination = content switch
        {
            DiscoverPage => "discover",
            InstalledPage => "installed",
            UpdatesPage => "updates",
            ActivityPage => "activity",
            SourcesPage => "sources",
            SettingsPage => "settings",
            _ => null
        };

        if (destination is not null)
        {
            SynchronizeNavigationSelection(destination);
        }
    }

    private void AddGlobalAccelerator(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = VirtualKeyModifiers.Control
        };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void FocusDiscoverSearch()
    {
        NavigateTo("discover");
        _discoverPage?.FocusSearch();
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException);

    private sealed record AgreementAcceptance(
        bool Accepted,
        bool AcceptedSourceAgreements,
        string? AcceptedSourceAgreementFingerprint,
        bool AcceptedPackageAgreements,
        string? AcceptedPackageAgreementFingerprint);
}
