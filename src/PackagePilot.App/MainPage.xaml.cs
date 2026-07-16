using System.Collections.Specialized;
using System.ComponentModel;
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
    private bool _syncScheduled;
    private bool _synchronizingNavigationSelection;
    private readonly IPrivilegedSourceManagementBroker? _sourceManagementBroker;
    private readonly IAppLifetimeController? _appLifetimeController;
    private readonly IAppLifetimeActivityGate _lifetimeActivityGate;
    private readonly IOperationDiagnosticsService? _operationDiagnosticsService;
    private bool _operationDiagnosticDialogOpen;

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

        ContentFrame.CacheSize = 8;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SubscribeToCollection(ViewModel.SearchResults);
        SubscribeToCollection(ViewModel.InstalledPackages);
        SubscribeToCollection(ViewModel.InstalledApps);
        SubscribeToCollection(ViewModel.InstalledAppProviders);
        SubscribeToCollection(ViewModel.AvailableUpdates);
        SubscribeToCollection(ViewModel.SourceStatuses);
        SubscribeToCollection(ViewModel.PendingOperations);
        SubscribeToCollection(ViewModel.OperationHistory);

        AddNavigationAccelerator(VirtualKey.Number1, "discover");
        AddNavigationAccelerator(VirtualKey.Number2, "installed");
        AddNavigationAccelerator(VirtualKey.Number3, "updates");
        AddNavigationAccelerator(VirtualKey.Number4, "activity");
        AddNavigationAccelerator(VirtualKey.Number5, "sources");
        AddNavigationAccelerator(VirtualKey.Number6, "settings");
        AddGlobalAccelerator(VirtualKey.R, () => OnRefreshRequested(this, EventArgs.Empty));
        AddGlobalAccelerator(VirtualKey.F, FocusDiscoverSearch);
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
        SyncViewData();
    }

    private async Task ApplyActivationAsync(AppActivationRequest request)
    {
        NavigateTo(ToNavigationTag(request.Destination));

        if (!string.IsNullOrWhiteSpace(request.SearchQuery))
        {
            ViewModel.SearchText = request.SearchQuery;
            _discoverPage?.SetSearchQuery(request.SearchQuery);
            await ViewModel.SearchAsync();
            SyncViewData();
        }

        if (request.CheckForUpdates)
        {
            await ViewModel.RefreshUpdatesAsync();
            SyncViewData();
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
                    page.ViewDiagnosticRequested += OnViewDiagnosticRequested;
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
        ScheduleSyncViewData();
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
        SyncViewData();
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
        var item = ToViewItem(package, e.Package.ActionLabel, details);
        if (sender is DiscoverPage discoverPage)
        {
            discoverPage.ShowPackageDetails(item);
        }
        else if (sender is InstalledPage installedPage)
        {
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

        SyncViewData();
    }

    private async Task LoadManagedSourcesAsync()
    {
        _sourcesPage?.SetLoading(true);
        await ViewModel.RefreshManagedSourcesAsync();
        _sourcesPage?.SetLoading(false);
        SyncViewData();
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
        SyncViewData();
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
        SyncViewData();
    }

    private async void OnPackageActionRequested(object? sender, PackageActionRequestedEventArgs e)
    {
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

        var kind = e.Package.RequestedOperationKind ??
            (e.Package.ActionLabel switch
            {
                "Update" or "Retry" => PackageOperationKind.Upgrade,
                "Uninstall" => PackageOperationKind.Uninstall,
                _ => PackageOperationKind.Install
            });

        if (ViewModel.IsPackageMutationBlocked(package.Key))
        {
            ShowPageStatus(
                "Already queued",
                $"{package.Name} already has this operation queued or running. Follow it in Activity.",
                InfoBarSeverity.Informational);
            SyncViewData();
            return;
        }

        if (kind == PackageOperationKind.Uninstall && !await ConfirmUninstallAsync(package))
        {
            return;
        }

        if (kind != PackageOperationKind.Uninstall && !await ConfirmElevationAsync(package))
        {
            return;
        }

        var acceptance = kind == PackageOperationKind.Uninstall
            ? new AgreementAcceptance(true, false, false)
            : await ConfirmAgreementsAsync(package);

        if (!acceptance.Accepted)
        {
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
                architecture);
        }
        catch (MutationRecoveryUnavailableException)
        {
            ShowMutationRecoveryUnavailable();
            SyncViewData();
            return;
        }
        catch (DuplicatePackageOperationException)
        {
            ShowPageStatus(
                "Already queued",
                $"{package.Name} already has this operation queued or running. Follow it in Activity.",
                InfoBarSeverity.Informational);
            SyncViewData();
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowPageStatus(
                "Could not queue package operation",
                exception.Message,
                InfoBarSeverity.Warning);
            SyncViewData();
            return;
        }

        ViewModel.SetStatusMessage($"{package.Name} was added to the operation queue.");
        if (kind == PackageOperationKind.Upgrade && sender is UpdatesPage)
        {
            _updatesPage?.ShowOperationStatus(
                "Update queued",
                $"{package.Name} is queued. Its row now shows progress, and full details remain available in Activity.",
                InfoBarSeverity.Informational);
        }
        UpdateShellChrome();
        SyncViewData();
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
        SyncViewData();
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
                    architecture)),
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
            SyncViewData();
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
        SyncViewData();
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

    private async void OnViewDiagnosticRequested(
        object? sender,
        OperationDiagnosticRequestedEventArgs e)
    {
        if (_operationDiagnosticDialogOpen || _operationDiagnosticsService is null)
        {
            return;
        }

        var operation = ViewModel.OperationHistory.FirstOrDefault(result =>
            result.OperationId == e.OperationId && result.EffectiveDiagnostic is not null);
        if (operation is null)
        {
            ShowPageStatus(
                "Log unavailable",
                "This activity is no longer retained.",
                InfoBarSeverity.Warning);
            return;
        }

        _operationDiagnosticDialogOpen = true;
        using var cancellation = new CancellationTokenSource();
        var progress = new ProgressRing
        {
            IsActive = true,
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var loadingText = new TextBlock
        {
            Text = "Loading operation diagnostics...",
            TextAlignment = TextAlignment.Center
        };
        var loadingPanel = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(16)
        };
        loadingPanel.Children.Add(progress);
        loadingPanel.Children.Add(loadingText);

        var notice = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(notice, AutomationLiveSetting.Polite);
        var rootSize = XamlRoot?.Size;
        var maximumLogWidth = rootSize is { } size && size.Width > 0
            ? Math.Max(0, Math.Min(760, size.Width - 64))
            : 760;
        var maximumLogHeight = rootSize is { } heightSize && heightSize.Height > 0
            ? Math.Max(0, Math.Min(480, heightSize.Height - 180))
            : 480;
        var logText = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            MaxWidth = maximumLogWidth,
            MaxHeight = maximumLogHeight,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(logText, "Operation diagnostic details");
        ScrollViewer.SetHorizontalScrollBarVisibility(logText, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(logText, ScrollBarVisibility.Auto);

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(loadingPanel);
        content.Children.Add(notice);
        content.Children.Add(logText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Operation diagnostics",
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };

        async void LoadDiagnostic(object? _, ContentDialogOpenedEventArgs __)
        {
            try
            {
                var document = await _operationDiagnosticsService.ReadAsync(
                    operation,
                    cancellation.Token);
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                dialog.Title = document.Title;
                notice.Text = document.IsTruncated
                    ? $"{document.Notice} Earlier content was omitted to keep the viewer responsive."
                    : document.Notice;
                notice.Visibility = Visibility.Visible;
                logText.Text = document.Text;
                logText.Header = document.ProviderLabel;
                logText.Visibility = Visibility.Visible;
                loadingPanel.Visibility = Visibility.Collapsed;
                _ = logText.Focus(FocusState.Programmatic);
            }
            catch (OperationCanceledException)
            {
                // Closing the dialog cancels the bounded read.
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                loadingPanel.Visibility = Visibility.Collapsed;
                notice.Text = $"The operation log could not be read (0x{exception.HResult:X8}).";
                notice.Visibility = Visibility.Visible;
            }
        }

        void CancelDiagnostic(object? _, ContentDialogClosedEventArgs __) => cancellation.Cancel();
        dialog.Opened += LoadDiagnostic;
        dialog.Closed += CancelDiagnostic;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowPageStatus(
                "Log unavailable",
                $"The operation log viewer could not be opened (0x{exception.HResult:X8}).",
                InfoBarSeverity.Warning);
        }
        finally
        {
            cancellation.Cancel();
            dialog.Opened -= LoadDiagnostic;
            dialog.Closed -= CancelDiagnostic;
            _operationDiagnosticDialogOpen = false;
        }
    }

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
            SyncViewData();
        }

        AppSettingChanged?.Invoke(this, e);
    }

    private void OnOpenActivityClick(object sender, RoutedEventArgs e)
    {
        NavigateTo("activity");
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

    private async Task<AgreementAcceptance> ConfirmAgreementsAsync(PackageSummary package)
    {
        await ViewModel.SelectPackageAsync(package);
        var sourceAgreements = (ViewModel.SelectedDetails?.Agreements ?? [])
            .Where(item => item.Kind == AgreementKind.Source)
            .ToArray();
        var acceptedSource = ViewModel.IsSourceAgreementAccepted(package.Key.SourceId)
            || sourceAgreements.Length == 0;
        if (!acceptedSource)
        {
            acceptedSource = await ConfirmAgreementSetAsync(
                $"Source agreements for {package.Name}",
                "Review the source terms before Package Pilot reconnects to retrieve package metadata.",
                sourceAgreements,
                "Accept source terms");
            if (!acceptedSource)
            {
                return new AgreementAcceptance(false, false, false);
            }

            ViewModel.AcceptPackageSourceAgreement(package, sourceAgreements);
            await ViewModel.SelectPackageAcceptingSourceAgreementsAsync(package);
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

        return new AgreementAcceptance(
            acceptedPackage,
            ViewModel.IsSourceAgreementAccepted(package.Key.SourceId)
                || (acceptedSource && sourceAgreements.Length > 0),
            acceptedPackage && packageAgreements.Length > 0);
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
        ScheduleSyncViewData();
    }

    private void SubscribeToCollection(INotifyCollectionChanged collection)
    {
        collection.CollectionChanged += (_, _) => ScheduleSyncViewData();
    }

    private void ScheduleSyncViewData()
    {
        if (_syncScheduled)
        {
            return;
        }

        _syncScheduled = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _syncScheduled = false;
                SyncViewData();
            }))
        {
            _syncScheduled = false;
        }
    }

    private void UpdateShellChrome()
    {
        StatusText.Text = ViewModel.StatusMessage;
        ActivitySummaryText.Text = ViewModel.ActivitySummary;
        ActivityProgress.IsActive = ViewModel.HasActiveOperation;
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

    private void SyncViewData()
    {
        if (_discoverPage is not null)
        {
            _discoverPage.SetResults(ViewModel.SearchResults.Select(item => ToViewItem(item)));
            _discoverPage.SetLoading(ViewModel.IsBusy);
            _discoverPage.SetMutationActionsAvailable(ViewModel.CanQueuePackageMutations);
        }

        if (_installedPage is not null)
        {
            _installedPage.SetPackages(
                ViewModel.InstalledAppProviders.Count > 0 || ViewModel.InstalledApps.Count > 0
                    ? ViewModel.InstalledApps.Select(ToViewItem)
                    : ViewModel.InstalledPackages.Select(item => ToViewItem(item, "Uninstall")));
            _installedPage.SetMutationActionsAvailable(ViewModel.CanQueuePackageMutations);
        }

        if (ContentFrame.Content is PackageDetailsPage detailsPage)
        {
            detailsPage.SetMutationActionsAvailable(ViewModel.CanQueuePackageMutations);
        }

        if (_updatesPage is not null)
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
                    ViewModel.GetMutationVerificationPhase(item.Key))));
            _updatesPage.SetCheckState(
                ViewModel.UpdatesCheckState,
                ViewModel.LastUpdateCheckAt,
                ViewModel.UpdateCheckError);
        }

        if (_activityPage is not null)
        {
            var operations = new List<OperationListItem>();
            if (ViewModel.CurrentOperation is { } current)
            {
                operations.Add(ToViewItem(current));
            }
            operations.AddRange(ViewModel.PendingOperations.Select(ToViewItem));
            operations.AddRange(ViewModel.OperationHistory.Select(OperationRowProjector.FromResult));
            _activityPage.SetOperations(operations);
        }

        if (_settingsPage is not null)
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


        if (_sourcesPage is not null)
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

        UpdateShellChrome();
    }

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
        var action = app.PrimaryAction;
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
            PackageId = app.Id,
            InstalledAppId = app.Id,
            Source = string.Join(", ", providers),
            InstalledVersion = app.VersionDisplay,
            Status = app.HasMultipleVersions ? "Multiple versions" : "Installed",
            ActionLabel = action?.Label ?? "Managed by Windows",
            IsActionEnabled = action is not null
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

    private static OperationListItem ToViewItem(OperationQueueEntry entry) => new()
    {
        OperationId = entry.Operation.Id,
        PackageName = entry.Operation.DisplayName,
        PackageId = entry.Operation.EffectiveTarget?.Id ?? entry.Operation.Package.Id,
        Action = entry.Operation.Kind.ToString(),
        Status = entry.Progress.State.ToString(),
        Detail = entry.Progress.Message ?? (entry.Progress.CanCancel ? "Waiting or downloading" : "The installer controls completion"),
        Timestamp = entry.Progress.Timestamp.LocalDateTime.ToString("g"),
        Progress = entry.Progress.Percent ?? 0,
        IsActive = entry.Progress.State is PackageOperationState.Resolving
            or PackageOperationState.Downloading
            or PackageOperationState.Installing
            or PackageOperationState.Upgrading
            or PackageOperationState.Uninstalling,
        IsIndeterminate = entry.Progress.Percent is null && entry.Progress.State is not PackageOperationState.Queued,
        CanCancel = entry.Progress.CanCancel
    };

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
        bool AcceptedPackageAgreements);
}
