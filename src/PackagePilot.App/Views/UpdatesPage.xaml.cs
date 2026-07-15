using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PackagePilot.Core.Models;

namespace PackagePilot.App.Views;

public sealed partial class UpdatesPage : Page
{
    private bool _changingSelection;
    private bool _ownsCheckStatus;
    private UpdateCheckState _checkState = UpdateCheckState.NotChecked;
    private DateTimeOffset? _lastCheckedAt;
    private string? _checkError;

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
        _ownsCheckStatus = false;
        StatusBanner.Title = title;
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
    }

    public void SetCheckState(
        UpdateCheckState state,
        DateTimeOffset? lastCheckedAt,
        string? error)
    {
        var changed = _checkState != state
            || _lastCheckedAt != lastCheckedAt
            || !string.Equals(_checkError, error, StringComparison.Ordinal);
        if (!changed)
        {
            return;
        }

        _checkState = state;
        _lastCheckedAt = lastCheckedAt;
        _checkError = error;
        UpdateState();

        switch (state)
        {
            case UpdateCheckState.Failed:
                ShowCheckStatus(
                    "Update check failed",
                    string.IsNullOrWhiteSpace(error)
                        ? "Package Pilot kept any previously saved results. Try again when you're ready."
                        : $"Package Pilot kept any previously saved results. {error}",
                    InfoBarSeverity.Warning);
                break;
            case UpdateCheckState.Stale:
                ShowCheckStatus(
                    "Saved update results",
                    "These results are more than 24 hours old. Check again to refresh them.",
                    InfoBarSeverity.Informational);
                break;
            default:
                if (_ownsCheckStatus)
                {
                    StatusBanner.IsOpen = false;
                    _ownsCheckStatus = false;
                }
                break;
        }
    }

    public void SetUpdates(IEnumerable<PackageListItem> updates)
    {
        var snapshot = updates.ToArray();
        if (PackageListItemComparer.HaveSameRows(AvailableUpdates, snapshot))
        {
            return;
        }

        AvailableUpdates.CollectionChanged -= OnUpdatesChanged;
        AvailableUpdates.Clear();
        foreach (var update in snapshot)
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
        HeaderCheckProgress.IsActive = _checkState == UpdateCheckState.Checking;
        HeaderCheckProgress.Visibility = HeaderCheckProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = _checkState != UpdateCheckState.Checking;
        EmptyStateProgress.IsActive = _checkState == UpdateCheckState.Checking;
        EmptyStateProgress.Visibility = EmptyStateProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateIconContainer.Visibility = _checkState == UpdateCheckState.Checking
            ? Visibility.Collapsed
            : Visibility.Visible;
        SelectAllBox.IsEnabled = hasUpdates;
        UpdateSummaryText.Text = AvailableUpdates.Count switch
        {
            0 when _checkState == UpdateCheckState.Checking => "Checking for updates",
            0 when _checkState == UpdateCheckState.Current => "No updates available",
            0 when _checkState == UpdateCheckState.Stale => "Saved result needs refreshing",
            0 when _checkState == UpdateCheckState.Failed => "Update check failed",
            0 => "Updates not checked yet",
            1 => "1 update available",
            _ => $"{AvailableUpdates.Count} updates available"
        };

        LastCheckedText.Text = _lastCheckedAt is { } checkedAt
            ? $"Last checked {checkedAt.ToLocalTime():g}."
            : "Updates have not been checked yet.";

        switch (_checkState)
        {
            case UpdateCheckState.Checking:
                EmptyStateTitle.Text = "Checking for updates";
                EmptyStateMessage.Text = "You can keep using Package Pilot while this finishes.";
                break;
            case UpdateCheckState.Current:
                EmptyStateIcon.Glyph = "\uE73E";
                EmptyStateTitle.Text = "You're up to date";
                EmptyStateMessage.Text = "WinGet did not find any newer package versions.";
                break;
            case UpdateCheckState.Stale:
                EmptyStateIcon.Glyph = "\uE823";
                EmptyStateTitle.Text = "Saved results are out of date";
                EmptyStateMessage.Text = "Check again before relying on this empty result.";
                break;
            case UpdateCheckState.Failed:
                EmptyStateIcon.Glyph = "\uE783";
                EmptyStateTitle.Text = "Couldn't check for updates";
                EmptyStateMessage.Text = string.IsNullOrWhiteSpace(_checkError)
                    ? "Try again when your package sources are available."
                    : _checkError;
                break;
            default:
                EmptyStateIcon.Glyph = "\uE895";
                EmptyStateTitle.Text = "Updates not checked yet";
                EmptyStateMessage.Text = "Check when you're ready. Package Pilot won't block startup to scan for updates.";
                break;
        }

        UpdateSelectionState();
    }

    private void ShowCheckStatus(string title, string message, InfoBarSeverity severity)
    {
        _ownsCheckStatus = true;
        StatusBanner.Title = title;
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
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
