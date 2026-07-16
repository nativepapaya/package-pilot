using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.App.Views;

public sealed partial class UpdatesPage : Page
{
    private bool _changingSelection;
    private bool _ownsCheckStatus;
    private bool _ownsOperationStatus;
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
        _ownsOperationStatus = false;
        StatusBanner.Title = title;
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
    }

    public void ShowOperationStatus(
        string title,
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _ownsCheckStatus = false;
        _ownsOperationStatus = true;
        SetOperationStatus(title, message, severity);
        if (AvailableUpdates.Any(item => item.OperationState is not null))
        {
            UpdateOperationStatus();
        }
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

        if (PackageListItemComparer.HaveSameRowsExceptOperationFeedback(
                AvailableUpdates,
                snapshot))
        {
            for (var index = 0; index < AvailableUpdates.Count; index++)
            {
                AvailableUpdates[index].ApplyOperationFeedback(snapshot[index]);
            }
            UpdateState();
            return;
        }

        var selectedPackages = UpdatesList.SelectedItems
            .OfType<PackageListItem>()
            .Where(item => item.WingetPackage is not null)
            .Select(item => item.WingetPackage!)
            .ToHashSet();

        _changingSelection = true;
        AvailableUpdates.CollectionChanged -= OnUpdatesChanged;
        AvailableUpdates.Clear();
        foreach (var update in snapshot)
        {
            AvailableUpdates.Add(update);
        }
        AvailableUpdates.CollectionChanged += OnUpdatesChanged;
        foreach (var update in AvailableUpdates.Where(item =>
                     item.IsActionEnabled
                     && item.WingetPackage is { } package
                     && selectedPackages.Contains(package)))
        {
            UpdatesList.SelectedItems.Add(update);
        }
        _changingSelection = false;
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
        var actionableCount = AvailableUpdates.Count(item => item.IsActionEnabled);
        SelectAllBox.IsEnabled = actionableCount > 0;
        UpdateSummaryText.Text = AvailableUpdates.Count switch
        {
            0 when _checkState == UpdateCheckState.Checking => "Checking for updates",
            0 when _checkState == UpdateCheckState.Current => "No updates available",
            0 when _checkState == UpdateCheckState.Stale => "Saved result needs refreshing",
            0 when _checkState == UpdateCheckState.Failed => "Update check failed",
            0 => "Updates not checked yet",
            1 => FormatUpdateSummary("1 update found"),
            _ => FormatUpdateSummary($"{AvailableUpdates.Count} updates found")
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

        UpdateOperationStatus();
        UpdateSelectionState();
    }

    private void ShowCheckStatus(string title, string message, InfoBarSeverity severity)
    {
        _ownsCheckStatus = true;
        _ownsOperationStatus = false;
        StatusBanner.Title = title;
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
    }

    private void OnStatusBannerClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        _ownsCheckStatus = false;
        _ownsOperationStatus = false;
    }

    private void UpdateOperationStatus()
    {
        if (!_ownsOperationStatus)
        {
            return;
        }

        if (AvailableUpdates.Any(item => item.OperationState == PackageOperationState.Failed))
        {
            SetOperationStatus(
                "Update failed",
                "One or more updates failed. Retry here when ready, or open Activity for details.",
                InfoBarSeverity.Warning);
            return;
        }

        if (AvailableUpdates.Any(item => item.OperationState is
                PackageOperationState.Resolving
                or PackageOperationState.Downloading
                or PackageOperationState.Installing
                or PackageOperationState.Upgrading))
        {
            SetOperationStatus(
                "Updating apps",
                "Updates run one at a time. Completed items will be verified after the queue finishes.",
                InfoBarSeverity.Informational);
            return;
        }

        if (AvailableUpdates.Any(item => item.OperationState == PackageOperationState.Queued))
        {
            SetOperationStatus(
                "Updates queued",
                "Selected updates are waiting for their turn. Completed items will be verified after the queue finishes.",
                InfoBarSeverity.Informational);
            return;
        }

        if (AvailableUpdates.Any(item => item.OperationState == PackageOperationState.RebootRequired))
        {
            var outcomeUnknown = AvailableUpdates.Any(item =>
                item.OperationState == PackageOperationState.RebootRequired
                && item.VerificationPhase == MutationVerificationPhase.OutcomeUnknown);
            SetOperationStatus(
                outcomeUnknown ? "Restart required to verify" : "Restart required",
                outcomeUnknown
                    ? "Package Pilot could not confirm an update result. Restart Windows before checking it again."
                    : "Windows must restart to finish one or more updates.",
                InfoBarSeverity.Warning);
            return;
        }

        if (AvailableUpdates.Any(item =>
                item.OperationState == PackageOperationState.Completed
                && item.VerificationPhase == MutationVerificationPhase.OutcomeUnknown))
        {
            SetOperationStatus(
                "Checking update result",
                "Package Pilot is using a fresh read-only scan to determine what happened.",
                InfoBarSeverity.Informational);
            return;
        }

        if (AvailableUpdates.Any(item => item.OperationState == PackageOperationState.Completed))
        {
            SetOperationStatus(
                "Update finished",
                "Package Pilot is checking the result before changing the available-update list.",
                InfoBarSeverity.Success);
            return;
        }

        if (AvailableUpdates.Any(item => item.OperationState == PackageOperationState.Cancelled))
        {
            SetOperationStatus(
                "Update cancelled",
                "The update remains available and can be retried when you're ready.",
                InfoBarSeverity.Informational);
            return;
        }

        StatusBanner.IsOpen = false;
        _ownsOperationStatus = false;
    }

    private void SetOperationStatus(string title, string message, InfoBarSeverity severity)
    {
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

        var unavailableSelections = UpdatesList.SelectedItems
            .OfType<PackageListItem>()
            .Where(item => !item.IsActionEnabled)
            .ToArray();
        if (unavailableSelections.Length > 0)
        {
            _changingSelection = true;
            foreach (var item in unavailableSelections)
            {
                UpdatesList.SelectedItems.Remove(item);
            }
            _changingSelection = false;
        }

        var actionableCount = AvailableUpdates.Count(item => item.IsActionEnabled);
        var selectedCount = UpdatesList.SelectedItems
            .OfType<PackageListItem>()
            .Count(item => item.IsActionEnabled);
        ReviewButton.IsEnabled = selectedCount > 0;
        ReviewButton.Content = selectedCount > 0 ? $"Review selected ({selectedCount})" : "Review selected";

        _changingSelection = true;
        SelectAllBox.IsChecked = actionableCount > 0 && selectedCount == actionableCount;
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
            UpdatesList.SelectedItems.Clear();
            foreach (var update in AvailableUpdates.Where(item => item.IsActionEnabled))
            {
                UpdatesList.SelectedItems.Add(update);
            }
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
        if (sender is PackageRow row && row.Tag is PackageKey packageKey)
        {
            var package = AvailableUpdates.FirstOrDefault(item => item.WingetPackage == packageKey);
            if (package is not null)
            {
                PackageActionRequested?.Invoke(this, new PackageActionRequestedEventArgs(package));
            }
        }
    }

    private async void OnReviewSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = UpdatesList.SelectedItems
            .OfType<PackageListItem>()
            .Where(item => item.IsActionEnabled)
            .ToList();
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

    private string FormatUpdateSummary(string availableSummary)
    {
        var queued = AvailableUpdates.Count(item => item.OperationState == PackageOperationState.Queued);
        var running = AvailableUpdates.Count(item => item.OperationState is
            PackageOperationState.Resolving
            or PackageOperationState.Downloading
            or PackageOperationState.Installing
            or PackageOperationState.Upgrading);
        var verifying = AvailableUpdates.Count(item => item.OperationState == PackageOperationState.Completed);
        var failed = AvailableUpdates.Count(item => item.OperationState == PackageOperationState.Failed);
        var restartRequired = AvailableUpdates.Count(item => item.OperationState == PackageOperationState.RebootRequired);

        var details = new List<string>();
        AddSummary(details, queued, "queued");
        AddSummary(details, running, "updating");
        AddSummary(details, verifying, "verifying");
        AddSummary(details, failed, "failed");
        AddSummary(details, restartRequired, "requires restart", "require restart");
        return details.Count == 0
            ? availableSummary
            : $"{availableSummary} | {string.Join(" | ", details)}";
    }

    private static void AddSummary(
        ICollection<string> summaries,
        int count,
        string singular,
        string? plural = null)
    {
        if (count > 0)
        {
            summaries.Add($"{count} {(count == 1 ? singular : plural ?? singular)}");
        }
    }
}
