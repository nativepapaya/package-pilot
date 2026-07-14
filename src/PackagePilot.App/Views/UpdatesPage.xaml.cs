using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PackagePilot.App.Views;

public sealed partial class UpdatesPage : Page
{
    private bool _changingSelection;

    public UpdatesPage()
    {
        InitializeComponent();
        AvailableUpdates.CollectionChanged += OnUpdatesChanged;
        UpdateState();
    }

    public ObservableCollection<PackageListItem> AvailableUpdates { get; } = [];
    public event EventHandler? RefreshRequested;
    public event EventHandler<PackageActionRequestedEventArgs>? PackageActionRequested;
    public event EventHandler<BulkPackageActionRequestedEventArgs>? BulkUpdateRequested;

    public void ShowStatus(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusBanner.Title = title;
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
    }

    public void SetUpdates(IEnumerable<PackageListItem> updates)
    {
        AvailableUpdates.CollectionChanged -= OnUpdatesChanged;
        AvailableUpdates.Clear();
        foreach (var update in updates)
        {
            AvailableUpdates.Add(update);
        }
        AvailableUpdates.CollectionChanged += OnUpdatesChanged;
        UpdateState();
    }

    private void OnUpdatesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();
    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateState()
    {
        var hasUpdates = AvailableUpdates.Count > 0;
        UpdatesList.Visibility = hasUpdates ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasUpdates ? Visibility.Collapsed : Visibility.Visible;
        SelectAllBox.IsEnabled = hasUpdates;
        UpdateSummaryText.Text = AvailableUpdates.Count switch
        {
            0 => "No updates available",
            1 => "1 update available",
            _ => $"{AvailableUpdates.Count} updates available"
        };
        UpdateSelectionState();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_changingSelection)
        {
            UpdateSelectionState();
        }
    }

    private void UpdateSelectionState()
    {
        if (UpdatesList is null || ReviewButton is null || SelectAllBox is null)
        {
            return;
        }

        var selectedCount = UpdatesList.SelectedItems.Count;
        ReviewButton.IsEnabled = selectedCount > 0;
        ReviewButton.Content = selectedCount > 0 ? $"Review selected ({selectedCount})" : "Review selected";

        _changingSelection = true;
        SelectAllBox.IsChecked = AvailableUpdates.Count > 0 && selectedCount == AvailableUpdates.Count;
        _changingSelection = false;
    }

    private void OnSelectAllChanged(object sender, RoutedEventArgs e)
    {
        if (_changingSelection)
        {
            return;
        }

        _changingSelection = true;
        if (SelectAllBox.IsChecked == true)
        {
            UpdatesList.SelectAll();
        }
        else
        {
            UpdatesList.SelectedItems.Clear();
        }
        _changingSelection = false;
        UpdateSelectionState();
    }

    private void OnPackageActionInvoked(object? sender, EventArgs e)
    {
        if (sender is PackageRow row && row.Tag is string packageId)
        {
            var package = AvailableUpdates.FirstOrDefault(item => item.PackageId == packageId);
            if (package is not null)
            {
                PackageActionRequested?.Invoke(this, new PackageActionRequestedEventArgs(package));
            }
        }
    }

    private async void OnReviewSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = UpdatesList.SelectedItems.OfType<PackageListItem>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var packageNames = string.Join(Environment.NewLine, selected.Take(8).Select(package =>
            $"• {package.Name}  {package.VersionLabel}{(package.RequiresElevation ? "  — administrator approval may be required" : string.Empty)}"));
        if (selected.Count > 8)
        {
            packageNames += $"{Environment.NewLine}• and {selected.Count - 8} more";
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Update {selected.Count} package{(selected.Count == 1 ? string.Empty : "s")}?",
            Content = $"Package Pilot will run these updates sequentially:{Environment.NewLine}{Environment.NewLine}{packageNames}",
            PrimaryButtonText = "Start updates",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            BulkUpdateRequested?.Invoke(this, new BulkPackageActionRequestedEventArgs(selected));
        }
    }
}
