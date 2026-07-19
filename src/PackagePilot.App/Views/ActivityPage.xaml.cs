using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PackagePilot.App.ViewModels;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PackagePilot.App.Views;

public sealed partial class ActivityPage : Page
{
    private readonly KeyedCollectionReconciler<Guid, OperationListItem> _reconciler = new();
    private readonly KeyedCollectionReconciler<Guid, OperationListItem> _displayedReconciler = new();
    private OperationDetailsViewModel? _detailsViewModel;
    private OperationListItem? _selectedOperation;
    private string _filter = "recent";
    private bool _autoScrollPaused;

    public ActivityPage()
    {
        InitializeComponent();
        Operations.CollectionChanged += OnOperationsChanged;
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateState();
    }

    public ObservableCollection<OperationListItem> Operations { get; } = [];
    public ObservableCollection<OperationListItem> DisplayedOperations { get; } = [];
    public ObservableCollection<OperationDiagnosticLineViewItem> DiagnosticLines { get; } = [];
    public event EventHandler<OperationCancelRequestedEventArgs>? CancelOperationRequested;
    public event EventHandler? CancelQueuedRequested;
    public event EventHandler? ClearCompletedRequested;

    internal void ConfigureDiagnostics(
        IOperationDiagnosticsService? service,
        Func<Guid, OperationDiagnosticSelection?> resolve)
    {
        if (_detailsViewModel is not null)
        {
            _detailsViewModel.PropertyChanged -= OnDetailsPropertyChanged;
            _detailsViewModel.Dispose();
            _detailsViewModel = null;
        }

        if (service is null)
        {
            return;
        }

        _detailsViewModel = new OperationDetailsViewModel(service, resolve);
        _detailsViewModel.PropertyChanged += OnDetailsPropertyChanged;
    }

    public void SetOperations(IEnumerable<OperationListItem> operations)
    {
        var snapshot = operations.ToArray();
        Operations.CollectionChanged -= OnOperationsChanged;
        _reconciler.Reconcile(
            Operations,
            snapshot,
            static operation => operation.OperationId,
            static (current, replacement) => OperationRowProjector.CopyPresentation(current, replacement));
        Operations.CollectionChanged += OnOperationsChanged;
        UpdateState();
    }

    private void OnOperationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        var state = ActivityStateProjector.Project(Operations);
        RefreshFilter();
        ClearHistoryButton.IsEnabled = state.CanClearCompleted;
        ToolTipService.SetToolTip(
            ClearHistoryButton,
            state.HasUnresolvedVerification
                ? "Complete pending verification before clearing activity and diagnostics."
                : "Remove completed activity and app-owned diagnostic logs.");
        CancelQueuedButton.IsEnabled = state.CanCancelQueued;
        QueueSummaryText.Text = state.Summary;
    }

    private void RefreshFilter()
    {
        var filtered = Operations.Where(operation => _filter switch
        {
            "active" => !operation.IsHistory,
            "attention" => operation.IsVerificationPending
                || operation.Status.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                || operation.Status.Contains("Restart", StringComparison.OrdinalIgnoreCase),
            _ => true
        }).ToArray();
        _displayedReconciler.Reconcile(
            DisplayedOperations,
            filtered,
            static operation => operation.OperationId,
            static (current, replacement) => OperationRowProjector.CopyPresentation(current, replacement));

        var hasRows = DisplayedOperations.Count > 0;
        ActivityList.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        if (!hasRows && Operations.Count > 0)
        {
            EmptyStateTitle.Text = "Nothing in this view";
            EmptyStateMessage.Text = _filter switch
            {
                "active" => "No package operations are queued or running.",
                "attention" => "No activity currently needs your attention.",
                _ => "No recent package activity."
            };
        }
        else
        {
            EmptyStateTitle.Text = "No package activity yet";
            EmptyStateMessage.Text = "Installs, updates, removals, failures, and reboot requirements will appear here.";
        }
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string filter } selected)
        {
            return;
        }

        _filter = filter;
        ActiveFilter.IsChecked = ReferenceEquals(selected, ActiveFilter);
        AttentionFilter.IsChecked = ReferenceEquals(selected, AttentionFilter);
        RecentFilter.IsChecked = ReferenceEquals(selected, RecentFilter);
        RefreshFilter();
    }

    private void OnActivitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActivityList.SelectedItem is OperationListItem operation)
        {
            _ = ShowOperationDetailsAsync(operation);
        }
    }

    private async Task ShowOperationDetailsAsync(OperationListItem operation)
    {
        _selectedOperation = operation;
        DetailsTitle.Text = operation.PackageName;
        DetailsSubtitle.Text = $"{operation.Action} · {operation.Status}";
        SummaryStatus.Text = operation.Status;
        SummaryDetail.Text = operation.Detail;
        SummaryPackageId.Text = operation.PackageId;
        DetailsPane.Visibility = Visibility.Visible;
        UpdateResponsiveDetails();

        DiagnosticNotice.IsOpen = !operation.CanViewDiagnostic || _detailsViewModel is null;
        DiagnosticNotice.Title = "Provider log unavailable";
        DiagnosticNotice.Message = "This activity does not expose a supported read-only diagnostic source.";
        if (operation.CanViewDiagnostic && _detailsViewModel is not null)
        {
            await _detailsViewModel.SelectAsync(operation.OperationId);
        }
        else
        {
            DiagnosticLines.Clear();
        }
    }

    private void OnCancelOperationClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid operationId })
        {
            var operation = Operations.FirstOrDefault(item => item.OperationId == operationId && item.CanCancel);
            if (operation is not null)
            {
                CancelOperationRequested?.Invoke(this, new OperationCancelRequestedEventArgs(operation));
            }
        }
    }

    private void OnViewDiagnosticClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid operationId })
        {
            var operation = Operations.FirstOrDefault(item =>
                item.OperationId == operationId && item.CanViewDiagnostic);
            if (operation is not null)
            {
                if (!ReferenceEquals(ActivityList.SelectedItem, operation))
                {
                    ActivityList.SelectedItem = operation;
                }
                else
                {
                    _ = ShowOperationDetailsAsync(operation);
                }
            }
        }
    }

    private void OnDetailsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_detailsViewModel is null)
        {
            return;
        }

        DiagnosticProgress.Visibility = _detailsViewModel.IsLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
        DiagnosticRefreshStatus.Text = _detailsViewModel.RefreshStatus;
        DiagnosticNotice.IsOpen = !string.IsNullOrWhiteSpace(_detailsViewModel.Error)
            || !string.IsNullOrWhiteSpace(_detailsViewModel.Document?.Notice);
        DiagnosticNotice.Title = !string.IsNullOrWhiteSpace(_detailsViewModel.Error)
            ? "Log read failed"
            : _detailsViewModel.Document?.ProviderLabel ?? "Operation diagnostics";
        DiagnosticNotice.Message = !string.IsNullOrWhiteSpace(_detailsViewModel.Error)
            ? _detailsViewModel.Error
            : _detailsViewModel.Document?.Notice ?? string.Empty;
        RefreshDiagnosticLines();
    }

    private void RefreshDiagnosticLines()
    {
        if (_detailsViewModel is null)
        {
            DiagnosticLines.Clear();
            return;
        }

        var query = LogSearchBox.Text.Trim();
        var lines = _detailsViewModel.Lines
            .Where(line => string.IsNullOrWhiteSpace(query)
                || line.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(line => new OperationDiagnosticLineViewItem
            {
                Index = line.Index,
                Text = line.Text,
                Severity = line.Severity,
                SeverityBrush = ResolveSeverityBrush(line.Severity)
            })
            .ToArray();
        DiagnosticLines.Clear();
        foreach (var line in lines)
        {
            DiagnosticLines.Add(line);
        }

        if (!_autoScrollPaused && DiagnosticLines.Count > 0)
        {
            DiagnosticLinesList.ScrollIntoView(DiagnosticLines[^1], ScrollIntoViewAlignment.Default);
        }
    }

    private static Brush ResolveSeverityBrush(OperationDiagnosticSeverity severity) =>
        (Brush)Application.Current.Resources[severity switch
        {
            OperationDiagnosticSeverity.Error => "PackagePilotCriticalBrush",
            OperationDiagnosticSeverity.Warning => "PackagePilotWarningBrush",
            OperationDiagnosticSeverity.Trace => "PackagePilotTextTertiaryBrush",
            _ => "PackagePilotTextSecondaryBrush"
        }];

    private void OnLogSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        RefreshDiagnosticLines();

    private void OnPauseAutoScrollClick(object sender, RoutedEventArgs e)
    {
        _autoScrollPaused = PauseAutoScrollButton.IsChecked == true;
        PauseAutoScrollButton.Content = _autoScrollPaused ? "Resume" : "Pause";
        AutomationProperties.SetName(
            PauseAutoScrollButton,
            _autoScrollPaused ? "Resume log auto-scroll" : "Pause log auto-scroll");
        if (!_autoScrollPaused && DiagnosticLines.Count > 0)
        {
            DiagnosticLinesList.ScrollIntoView(DiagnosticLines[^1]);
        }
    }

    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        if (_detailsViewModel?.Document is not { } document)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(document.Text);
        Clipboard.SetContent(package);
        DiagnosticRefreshStatus.Text = "Copied the redacted diagnostic text.";
    }

    private async void OnExportLogClick(object sender, RoutedEventArgs e)
    {
        if (_detailsViewModel?.Document is not { } document)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"package-pilot-{_detailsViewModel.OperationId:N}",
            DefaultFileExtension = ".txt"
        };
        picker.FileTypeChoices.Add("Text log", [".txt"]);
        if (Application.Current is App { CurrentWindow: { } window })
        {
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await FileIO.WriteTextAsync(file, document.Text);
            DiagnosticRefreshStatus.Text = $"Exported {file.Name}.";
        }
    }

    private async void OnRefreshDiagnosticClick(object sender, RoutedEventArgs e)
    {
        if (_detailsViewModel is not null)
        {
            await _detailsViewModel.RefreshNowAsync();
        }
    }

    private void OnDetailsCloseClick(object sender, RoutedEventArgs e)
    {
        _detailsViewModel?.Close();
        _selectedOperation = null;
        ActivityList.SelectedItem = null;
        DetailsPane.Visibility = Visibility.Collapsed;
        DiagnosticLines.Clear();
        UpdateResponsiveDetails();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveDetails();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_selectedOperation is { CanViewDiagnostic: true } selected
            && _detailsViewModel is not null)
        {
            _ = _detailsViewModel.SelectAsync(selected.OperationId);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _detailsViewModel?.Close();
        DiagnosticLines.Clear();
    }

    private void UpdateResponsiveDetails()
    {
        var hasDetails = _selectedOperation is not null && DetailsPane.Visibility == Visibility.Visible;
        var wide = ActualWidth >= 900;
        DetailsColumn.Width = hasDetails
            ? wide ? new GridLength(410) : new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        ActivityContent.ColumnDefinitions[0].Width = hasDetails && !wide
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        ListSurface.Visibility = hasDetails && !wide ? Visibility.Collapsed : Visibility.Visible;
        DetailsBackButton.Visibility = hasDetails && !wide ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCancelQueuedClick(object sender, RoutedEventArgs e) =>
        CancelQueuedRequested?.Invoke(this, EventArgs.Empty);

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (ActivityStateProjector.Project(Operations).CanClearCompleted)
        {
            ClearCompletedRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class OperationDiagnosticLineViewItem
{
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;
    public OperationDiagnosticSeverity Severity { get; set; }
    public Brush SeverityBrush { get; set; } = null!;
}
