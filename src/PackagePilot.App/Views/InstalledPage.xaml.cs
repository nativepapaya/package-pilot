using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Diagnostics;
using PackagePilot.App.Services;

namespace PackagePilot.App.Views;

public sealed partial class InstalledPage : Page
{
    private PackageListItem? _selectedPackage;
    private string _providerFilter = "all";
    private bool _isViewReady;

    public InstalledPage()
    {
        InitializeComponent();

        // Establish the initial selection before subscribing. ComboBox can raise
        // SelectionChanged while InitializeComponent is still connecting fields that
        // appear later in XAML, so all filter/sort events are wired only after it returns.
        SortBox.SelectedIndex = 0;
        TypeFilterBox.SelectedIndex = 0;
        InstalledSearchBox.TextChanged += OnFilterTextChanged;
        SortBox.SelectionChanged += OnSortChanged;
        TypeFilterBox.SelectionChanged += OnSortChanged;
        _isViewReady = true;
        SizeChanged += OnSizeChanged;
        UpdateDisplayedPackages();
    }

    public IReadOnlyList<PackageListItem> Packages { get; private set; } = Array.Empty<PackageListItem>();
    public IReadOnlyList<PackageListItem> DisplayedPackages { get; private set; } = Array.Empty<PackageListItem>();
    public event EventHandler? RefreshRequested;
    public event EventHandler<PackageActionRequestedEventArgs>? PackageActionRequested;
    public event EventHandler<PackageActionRequestedEventArgs>? PackageSelected;

    public void SetMutationActionsAvailable(bool available) =>
        DetailsPane.SetMutationActionsAvailable(available);

    public void ShowStatus(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusBanner.Title = title;
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
    }

    public void SetPackages(IEnumerable<PackageListItem> packages)
    {
        var snapshot = packages.ToArray();
        if (PackageListItemComparer.HaveSameRows(Packages, snapshot))
        {
            return;
        }

        Packages = snapshot;
        UpdateDisplayedPackages();
    }

    private void OnFilterTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => UpdateDisplayedPackages();
    private void OnSortChanged(object sender, SelectionChangedEventArgs e) => UpdateDisplayedPackages();
    private void OnShowWindowsManagedClick(object sender, RoutedEventArgs e) => UpdateDisplayedPackages();
    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnProviderFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem selected
            || selected.Tag is not string provider)
        {
            return;
        }

        _providerFilter = provider;
        foreach (var item in new[]
                 {
                     AllProvidersFilterItem,
                     WingetProviderFilterItem,
                     WindowsProviderFilterItem,
                     RegistryProviderFilterItem
                 })
        {
            item.IsChecked = ReferenceEquals(item, selected);
        }

        ProviderFilterText.Text = selected.Text;
        UpdateDisplayedPackages();
    }

    private void OnFocusSearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        InstalledSearchBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void UpdateDisplayedPackages()
    {
        var startedAt = Stopwatch.GetTimestamp();
        if (!_isViewReady)
        {
            return;
        }

        var query = InstalledSearchBox.Text.Trim();
        IEnumerable<PackageListItem> packages = Packages.Where(package =>
            string.IsNullOrWhiteSpace(query) ||
            package.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            package.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            package.PackageId.Contains(query, StringComparison.OrdinalIgnoreCase));

        packages = _providerFilter switch
        {
            "winget" => packages.Where(package =>
                package.Source.Contains("WinGet", StringComparison.OrdinalIgnoreCase)),
            "windows" => packages.Where(package =>
                package.Source.Contains("Microsoft Store", StringComparison.OrdinalIgnoreCase)
                || package.Source.Contains("MSIX", StringComparison.OrdinalIgnoreCase)),
            "registry" => packages.Where(package =>
                package.Source.Contains("registry", StringComparison.OrdinalIgnoreCase)),
            _ => packages
        };

        var type = (TypeFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
        packages = type switch
        {
            "store" => packages.Where(package =>
                package.Source.Contains("Microsoft Store", StringComparison.OrdinalIgnoreCase)),
            "msix" => packages.Where(package =>
                package.Source.Split(',', StringSplitOptions.TrimEntries)
                    .Contains("MSIX", StringComparer.OrdinalIgnoreCase)),
            "legacy" => packages.Where(package =>
                package.Source.Contains("registry", StringComparison.OrdinalIgnoreCase)),
            _ => packages
        };

        var packagesBeforeManagementFilter = packages.ToArray();
        var matchingWindowsManagedCount = InstalledPackageVisibility.CountWindowsManaged(
            packagesBeforeManagementFilter);
        var showWindowsManaged = ShowWindowsManagedToggle.IsChecked == true;
        packages = packagesBeforeManagementFilter.Where(package =>
            InstalledPackageVisibility.ShouldShow(package, showWindowsManaged));

        var sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string;
        packages = sort switch
        {
            "publisher" => packages.OrderBy(package => package.Publisher, StringComparer.OrdinalIgnoreCase),
            "version" => packages.OrderBy(package => package.InstalledVersion, StringComparer.OrdinalIgnoreCase),
            _ => packages.OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
        };

        // Assign one immutable snapshot instead of issuing a Clear plus one native collection
        // notification per installed package. Large inventories otherwise force repeated row
        // teardown/realization and can race asynchronous icon work during navigation.
        DisplayedPackages = packages.ToArray();
        InstalledList.ItemsSource = DisplayedPackages;

        var hasPackages = DisplayedPackages.Count > 0;
        InstalledList.Visibility = hasPackages ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasPackages ? Visibility.Collapsed : Visibility.Visible;
        var totalWindowsManagedCount = InstalledPackageVisibility.CountWindowsManaged(Packages);
        ShowWindowsManagedToggle.IsEnabled = totalWindowsManagedCount > 0;
        ShowWindowsManagedToggle.Text = totalWindowsManagedCount > 0
            ? $"Include Windows-managed apps ({totalWindowsManagedCount})"
            : "Include Windows-managed apps";
        AutomationProperties.SetName(
            ShowWindowsManagedToggle,
            showWindowsManaged
                ? "Windows-managed apps are included"
                : "Include Windows-managed apps");

        if (!hasPackages && !showWindowsManaged && matchingWindowsManagedCount > 0)
        {
            EmptyStateTitle.Text = "Windows-managed apps are hidden";
            EmptyStateDescription.Text = matchingWindowsManagedCount == 1
                ? "One matching app can only be managed through Windows or Microsoft Store. Show Windows-managed apps to review it."
                : $"{matchingWindowsManagedCount} matching apps can only be managed through Windows or Microsoft Store. Show Windows-managed apps to review them.";
        }
        else
        {
            EmptyStateTitle.Text = "No matching apps";
            EmptyStateDescription.Text = "Refresh to scan installed software, or clear the filter to see all detected packages.";
        }

        PackageCountText.Text = (!showWindowsManaged && matchingWindowsManagedCount > 0)
            ? $"{FormatInstalledCount(DisplayedPackages.Count, manageableOnly: true)} · {matchingWindowsManagedCount} Windows-managed hidden"
            : FormatInstalledCount(DisplayedPackages.Count, manageableOnly: !showWindowsManaged);
        PackagePilotUiEventSource.Log.FilterCompleted(
            "Installed",
            DisplayedPackages.Count,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private void OnPackageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstalledList.SelectedItem is not PackageListItem package)
        {
            return;
        }

        PackageSelected?.Invoke(this, new PackageActionRequestedEventArgs(package));
    }

    public void ShowPackageDetails(PackageListItem package)
    {
        _selectedPackage = package;
        if (ActualWidth >= 900)
        {
            DetailsPane.ShowPackage(package);
            UpdateResponsiveDetails();
        }
        else
        {
            Frame.Navigate(typeof(PackageDetailsPage), package);
            InstalledList.SelectedItem = null;
        }
    }

    public void SelectPackageById(string installedAppId)
    {
        var package = Packages.FirstOrDefault(candidate =>
            string.Equals(candidate.InstalledAppId, installedAppId, StringComparison.Ordinal));
        if (package is null)
        {
            ShowStatus(
                "Installed app unavailable",
                "Refresh Installed and try again.",
                InfoBarSeverity.Warning);
            return;
        }

        InstalledSearchBox.Text = string.Empty;
        if (package.IsManageabilityKnown && !package.IsManageableByPackagePilot)
        {
            ShowWindowsManagedToggle.IsChecked = true;
        }
        UpdateDisplayedPackages();
        InstalledList.SelectedItem = package;
        InstalledList.ScrollIntoView(package, ScrollIntoViewAlignment.Leading);
        ShowPackageDetails(package);
    }

    private async void OnPackageActionInvoked(object? sender, EventArgs e)
    {
        if (sender is PackageRow row && row.Tag is string packageId)
        {
            var package = Packages.FirstOrDefault(item => item.PackageId == packageId);
            if (package is not null)
            {
                await RequestActionAsync(package);
            }
        }
    }

    private async void OnDetailsPrimaryActionInvoked(object? sender, EventArgs e)
    {
        if (_selectedPackage is not null)
        {
            await RequestActionAsync(_selectedPackage);
        }
    }

    private Task RequestActionAsync(PackageListItem package)
    {
        PackageActionRequested?.Invoke(this, new PackageActionRequestedEventArgs(package));
        return Task.CompletedTask;
    }

    private void OnDetailsCloseRequested(object? sender, EventArgs e)
    {
        InstalledList.SelectedItem = null;
        _selectedPackage = null;
        UpdateResponsiveDetails();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveDetails();

    private void UpdateResponsiveDetails()
    {
        var showInline = ActualWidth >= 900 && _selectedPackage is not null;
        DetailsColumn.Width = showInline ? new GridLength(380) : new GridLength(0);
        DetailsPane.Visibility = showInline ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatInstalledCount(int count, bool manageableOnly) => count switch
    {
        0 => manageableOnly ? "No manageable apps shown" : "No installed apps shown",
        1 => manageableOnly ? "1 manageable app" : "1 installed app",
        _ => manageableOnly ? $"{count} manageable apps" : $"{count} installed apps"
    };
}
