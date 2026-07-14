using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using PackagePilot.App.ViewModels;
using PackagePilot.App.Views;
using PackagePilot.Core.Models;
using Windows.System;
using Windows.Storage;

namespace PackagePilot.App;

public sealed partial class MainPage : Page
{
    private DiscoverPage? _discoverPage;
    private InstalledPage? _installedPage;
    private UpdatesPage? _updatesPage;
    private ActivityPage? _activityPage;
    private SettingsPage? _settingsPage;
    private bool _initialized;
    private bool _syncScheduled;
    private bool _synchronizingNavigationSelection;

    public MainPage(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        ContentFrame.CacheSize = 8;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SubscribeToCollection(ViewModel.SearchResults);
        SubscribeToCollection(ViewModel.InstalledPackages);
        SubscribeToCollection(ViewModel.AvailableUpdates);
        SubscribeToCollection(ViewModel.SourceStatuses);
        SubscribeToCollection(ViewModel.PendingOperations);
        SubscribeToCollection(ViewModel.OperationHistory);

        AddNavigationAccelerator(VirtualKey.Number1, "discover");
        AddNavigationAccelerator(VirtualKey.Number2, "installed");
        AddNavigationAccelerator(VirtualKey.Number3, "updates");
        AddNavigationAccelerator(VirtualKey.Number4, "activity");
        AddNavigationAccelerator(VirtualKey.Number5, "settings");
        AddGlobalAccelerator(VirtualKey.R, () => OnRefreshRequested(this, EventArgs.Empty));
        AddGlobalAccelerator(VirtualKey.F, FocusDiscoverSearch);
    }

    public ShellViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ApplyStoredTheme();
        NavigateTo("discover");
        UpdateShellChrome();
        await ViewModel.InitializeAsync();
        UpdateShellChrome();
        SyncViewData();
    }

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
                }
                break;
            case SettingsPage page:
                if (!ReferenceEquals(_settingsPage, page))
                {
                    _settingsPage = page;
                    page.NavigationCacheMode = NavigationCacheMode.Required;
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

        var sourceAgreements = ViewModel.SourceStatuses
            .SelectMany(source => source.Agreements)
            .GroupBy(agreement => agreement.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (sourceAgreements.Length > 0
            && await ConfirmAgreementSetAsync(
                "WinGet source agreements",
                "One or more configured sources require consent before they can return packages.",
                sourceAgreements,
                "Accept and search again"))
        {
            await ViewModel.SearchAcceptingSourceAgreementsAsync();
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
        await ViewModel.RefreshAllAsync();
        SyncViewData();
    }

    private async void OnPackageActionRequested(object? sender, PackageActionRequestedEventArgs e)
    {
        var package = FindPackage(e.Package);
        if (package is null)
        {
            ShowPageStatus("Package unavailable", "Refresh the page and try again.", InfoBarSeverity.Warning);
            return;
        }

        var kind = e.Package.ActionLabel switch
        {
            "Update" => PackageOperationKind.Upgrade,
            "Uninstall" => PackageOperationKind.Uninstall,
            _ => PackageOperationKind.Install
        };

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
        ViewModel.EnqueueOperation(
            package,
            kind,
            acceptance.AcceptedSourceAgreements,
            acceptance.AcceptedPackageAgreements,
            scope,
            architecture);
        ViewModel.SetStatusMessage($"{package.Name} was added to the operation queue.");
        UpdateShellChrome();
        SyncViewData();
    }

    private async void OnBulkUpdateRequested(object? sender, BulkPackageActionRequestedEventArgs e)
    {
        var queued = 0;
        foreach (var viewPackage in e.Packages)
        {
            var package = FindPackage(viewPackage);
            if (package is null)
            {
                continue;
            }

            var acceptance = await ConfirmAgreementsAsync(package);
            if (!acceptance.Accepted)
            {
                continue;
            }

            var (scope, architecture) = ReadInstallPreferences();
            ViewModel.EnqueueOperation(
                package,
                PackageOperationKind.Upgrade,
                acceptance.AcceptedSourceAgreements,
                acceptance.AcceptedPackageAgreements,
                scope,
                architecture);
            queued++;
        }

        ViewModel.SetStatusMessage(queued == 0
            ? "No updates were queued."
            : $"Queued {queued} update{(queued == 1 ? string.Empty : "s")}.");
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

    private void OnClearCompletedRequested(object? sender, EventArgs e) => ViewModel.ClearHistory();

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
        var acceptedSource = ViewModel.HasAcceptedSourceAgreements || sourceAgreements.Length == 0;
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
            ViewModel.HasAcceptedSourceAgreements || (acceptedSource && sourceAgreements.Length > 0),
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
        return ViewModel.SearchResults
            .Concat(ViewModel.InstalledPackages)
            .Concat(ViewModel.AvailableUpdates)
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
        UpdatesBadge.Value = ViewModel.AvailableUpdates.Count > 0 ? ViewModel.AvailableUpdates.Count : -1;

        HealthBanner.IsOpen = _initialized && !ViewModel.IsReady && !ViewModel.IsBusy;
        HealthBanner.Title = ViewModel.HealthTitle;
        HealthBanner.Message = ViewModel.HealthMessage;
    }

    private void SyncViewData()
    {
        if (_discoverPage is not null)
        {
            _discoverPage.SetResults(ViewModel.SearchResults.Select(item => ToViewItem(item)));
            _discoverPage.SetLoading(ViewModel.IsBusy);
        }

        if (_installedPage is not null)
        {
            _installedPage.SetPackages(ViewModel.InstalledPackages.Select(item => ToViewItem(item, "Uninstall")));
        }

        if (_updatesPage is not null)
        {
            _updatesPage.SetUpdates(ViewModel.AvailableUpdates.Select(item => ToViewItem(item, "Update")));
        }

        if (_activityPage is not null)
        {
            var operations = new List<OperationListItem>();
            if (ViewModel.CurrentOperation is { } current)
            {
                operations.Add(ToViewItem(current));
            }
            operations.AddRange(ViewModel.PendingOperations.Select(ToViewItem));
            operations.AddRange(ViewModel.OperationHistory.Select(ToViewItem));
            _activityPage.SetOperations(operations);
        }

        if (_settingsPage is not null)
        {
            _settingsPage.SetCapabilitySummary(ViewModel.HealthMessage);
            ReplaceAll(_settingsPage.Sources, ViewModel.SourceStatuses.Select(source => new SourceHealthItem
            {
                Name = source.Name,
                Identifier = source.Id,
                Status = source.Health.ToString(),
                Detail = source.Message ?? (source.Health == SourceHealth.Healthy ? "Available" : "No diagnostic detail")
            }));
        }

        UpdateShellChrome();
    }

    private static PackageListItem ToViewItem(
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

    private static OperationListItem ToViewItem(OperationQueueEntry entry) => new()
    {
        OperationId = entry.Operation.Id,
        PackageName = entry.Operation.DisplayName,
        PackageId = entry.Operation.Package.Id,
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

    private static OperationListItem ToViewItem(OperationResult result) => new()
    {
        OperationId = result.OperationId,
        PackageName = result.Package.Id,
        PackageId = result.Package.Id,
        Action = result.Kind.ToString(),
        Status = result.State.ToString(),
        Detail = result.Error?.Message ?? (result.RebootRequired ? "Restart Windows to complete this operation." : "Completed"),
        Timestamp = result.CompletedAt.LocalDateTime.ToString("g"),
        Progress = result.IsSuccess ? 100 : 0,
        IsActive = false,
        IsIndeterminate = false,
        CanCancel = false
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

    private sealed record AgreementAcceptance(
        bool Accepted,
        bool AcceptedSourceAgreements,
        bool AcceptedPackageAgreements);
}
