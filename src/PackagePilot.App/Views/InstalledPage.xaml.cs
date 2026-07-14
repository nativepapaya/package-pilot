using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace PackagePilot.App.Views;

public sealed partial class InstalledPage : Page
{
    private PackageListItem? _selectedPackage;
    private bool _isViewReady;

    public InstalledPage()
    {
        InitializeComponent();

        // Establish the initial selection before subscribing. ComboBox can raise
        // SelectionChanged while InitializeComponent is still connecting fields that
        // appear later in XAML, so all filter/sort events are wired only after it returns.
        SortBox.SelectedIndex = 0;
        InstalledSearchBox.TextChanged += OnFilterTextChanged;
        SortBox.SelectionChanged += OnSortChanged;
        _isViewReady = true;
        SizeChanged += OnSizeChanged;
        UpdateDisplayedPackages();
    }

    public IReadOnlyList<PackageListItem> Packages { get; private set; } = Array.Empty<PackageListItem>();
    public IReadOnlyList<PackageListItem> DisplayedPackages { get; private set; } = Array.Empty<PackageListItem>();
    public event EventHandler? RefreshRequested;
    public event EventHandler<PackageActionRequestedEventArgs>? PackageActionRequested;
    public event EventHandler<PackageActionRequestedEventArgs>? PackageSelected;

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
        if (HaveSameRows(Packages, snapshot))
        {
            return;
        }

        Packages = snapshot;
        UpdateDisplayedPackages();
    }

    private void OnFilterTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => UpdateDisplayedPackages();
    private void OnSortChanged(object sender, SelectionChangedEventArgs e) => UpdateDisplayedPackages();
    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnFocusSearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        InstalledSearchBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void UpdateDisplayedPackages()
    {
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
        PackageCountText.Text = DisplayedPackages.Count switch
        {
            0 => "No packages shown",
            1 => "1 installed package",
            _ => $"{DisplayedPackages.Count} installed packages"
        };
    }

    private static bool HaveSameRows(
        IReadOnlyList<PackageListItem> current,
        IReadOnlyList<PackageListItem> replacement)
    {
        if (current.Count != replacement.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = replacement[index];
            if (!string.Equals(left.PackageId, right.PackageId, StringComparison.Ordinal)
                || !string.Equals(left.Source, right.Source, StringComparison.Ordinal)
                || !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                || !string.Equals(left.Publisher, right.Publisher, StringComparison.Ordinal)
                || !string.Equals(left.InstalledVersion, right.InstalledVersion, StringComparison.Ordinal)
                || !string.Equals(left.AvailableVersion, right.AvailableVersion, StringComparison.Ordinal)
                || !string.Equals(left.Status, right.Status, StringComparison.Ordinal)
                || !string.Equals(left.ActionLabel, right.ActionLabel, StringComparison.Ordinal)
                || left.IconUri != right.IconUri)
            {
                return false;
            }
        }

        return true;
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
}
