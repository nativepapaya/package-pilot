using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PackagePilot.Core.Models;
using PackagePilot.App.ViewModels;
using PackagePilot.App.Services;

namespace PackagePilot.App.Views;

public sealed partial class DiscoverPage : Page
{
    private PackageListItem? _selectedPackage;
    private readonly KeyedCollectionReconciler<PackageListItemKey, PackageListItem> _reconciler = new();

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
        LoadingBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        ResultCountText.Text = isLoading && Results.Count > 0
            ? $"Searching… showing {Results.Count} previous result{(Results.Count == 1 ? string.Empty : "s")}"
            : Results.Count switch
            {
                0 => "No results loaded",
                1 => "1 package",
                _ => $"{Results.Count} packages"
            };
    }

    public void FocusSearch() => PackageSearchBox.Focus(FocusState.Programmatic);

    public void SetSearchQuery(string query)
    {
        PackageSearchBox.Text = query?.Trim() ?? string.Empty;
        PackageSearchBox.Focus(FocusState.Programmatic);
    }

    public void SetResults(IEnumerable<PackageListItem> results)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var snapshot = results.ToArray();
        if (PackageListItemComparer.HaveSameRows(Results, snapshot))
        {
            return;
        }

        SynchronizeSelectedPackageState(snapshot);
        Results.CollectionChanged -= OnResultsChanged;
        _reconciler.Reconcile(
            Results,
            snapshot,
            static item => item.StableKey,
            static (current, replacement) => current.ApplyPresentation(replacement));
        Results.CollectionChanged += OnResultsChanged;
        UpdateResultState();
        PackagePilotUiEventSource.Log.SearchResultsPresented(
            Results.Count,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    public void ShowStatus(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusBanner.Title = title;
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
    }

    public void SetMutationActionsAvailable(bool available) =>
        DetailsPane.SetMutationActionsAvailable(available);

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

    private void OnSearchClick(object sender, RoutedEventArgs e) => RaiseSearchRequested();

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
        if (sender is PackageRow row && row.Tag is PackageKey packageKey)
        {
            var package = Results.FirstOrDefault(item =>
                item.WingetPackage is { } candidate && PackageKeysEqual(candidate, packageKey));
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

    private void SynchronizeSelectedPackageState(IReadOnlyList<PackageListItem> snapshot)
    {
        if (_selectedPackage?.WingetPackage is not { } selectedKey)
        {
            return;
        }

        var replacement = snapshot.FirstOrDefault(item =>
            item.WingetPackage is { } candidate && PackageKeysEqual(candidate, selectedKey));
        if (replacement is not null)
        {
            _selectedPackage.ApplyDiscoverState(replacement);
        }
    }

    private static bool PackageKeysEqual(PackageKey left, PackageKey right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase);

    private void UpdateResponsiveDetails()
    {
        var showInline = ActualWidth >= 900 && _selectedPackage is not null;
        DetailsColumn.Width = showInline ? new GridLength(380) : new GridLength(0);
        DetailsPane.Visibility = showInline ? Visibility.Visible : Visibility.Collapsed;
    }
}
