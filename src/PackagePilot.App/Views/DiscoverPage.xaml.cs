using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace PackagePilot.App.Views;

public sealed partial class DiscoverPage : Page
{
    private PackageListItem? _selectedPackage;

    public DiscoverPage()
    {
        InitializeComponent();
        Results.CollectionChanged += OnResultsChanged;
        SizeChanged += OnSizeChanged;
        UpdateResultState();
    }

    public ObservableCollection<PackageListItem> Results { get; } = [];
    public event EventHandler<SearchRequestedEventArgs>? SearchRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler<PackageActionRequestedEventArgs>? PackageActionRequested;
    public event EventHandler<PackageActionRequestedEventArgs>? PackageSelected;

    public void SetLoading(bool isLoading)
    {
        LoadingRing.IsActive = isLoading;
        LoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.IsEnabled = !isLoading;
    }

    public void FocusSearch() => PackageSearchBox.Focus(FocusState.Programmatic);

    public void SetSearchQuery(string query)
    {
        PackageSearchBox.Text = query?.Trim() ?? string.Empty;
        PackageSearchBox.Focus(FocusState.Programmatic);
    }

    public void SetResults(IEnumerable<PackageListItem> results)
    {
        var snapshot = results.ToArray();
        if (PackageListItemComparer.HaveSameRows(Results, snapshot))
        {
            return;
        }

        Results.CollectionChanged -= OnResultsChanged;
        Results.Clear();
        foreach (var result in snapshot)
        {
            Results.Add(result);
        }
        Results.CollectionChanged += OnResultsChanged;
        UpdateResultState();
    }

    public void ShowStatus(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusBanner.Title = title;
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        RaiseSearchRequested();
    }

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        RaiseSearchRequested();
    }

    private void RaiseSearchRequested() => SearchRequested?.Invoke(this, new SearchRequestedEventArgs(PackageSearchBox.Text.Trim()));

    private void OnFocusSearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        PackageSearchBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnResultsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateResultState();

    private void UpdateResultState()
    {
        var hasResults = Results.Count > 0;
        ResultsList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
        ResultCountText.Text = Results.Count switch
        {
            0 => "No results loaded",
            1 => "1 package",
            _ => $"{Results.Count} packages"
        };
    }

    private void OnPackageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not PackageListItem package)
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
            ResultsList.SelectedItem = null;
        }
    }

    private void OnPackageActionInvoked(object? sender, EventArgs e)
    {
        if (sender is PackageRow row && row.Tag is string packageId)
        {
            var package = Results.FirstOrDefault(item => item.PackageId == packageId);
            if (package is not null)
            {
                PackageActionRequested?.Invoke(this, new PackageActionRequestedEventArgs(package));
            }
        }
    }

    private void OnDetailsPrimaryActionInvoked(object? sender, EventArgs e)
    {
        if (_selectedPackage is not null)
        {
            PackageActionRequested?.Invoke(this, new PackageActionRequestedEventArgs(_selectedPackage));
        }
    }

    private void OnDetailsCloseRequested(object? sender, EventArgs e)
    {
        ResultsList.SelectedItem = null;
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
