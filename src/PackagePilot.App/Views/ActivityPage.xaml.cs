using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PackagePilot.App.Views;

public sealed partial class ActivityPage : Page
{
    public ActivityPage()
    {
        InitializeComponent();
        Operations.CollectionChanged += OnOperationsChanged;
        UpdateState();
    }

    public ObservableCollection<OperationListItem> Operations { get; } = [];
    public event EventHandler<OperationCancelRequestedEventArgs>? CancelOperationRequested;
    public event EventHandler<OperationDiagnosticRequestedEventArgs>? ViewDiagnosticRequested;
    public event EventHandler? CancelQueuedRequested;
    public event EventHandler? ClearCompletedRequested;

    public void SetOperations(IEnumerable<OperationListItem> operations)
    {
        var snapshot = operations.ToArray();
        if (Operations.Count == snapshot.Length
            && Operations.Select(item => item.OperationId)
                .SequenceEqual(snapshot.Select(item => item.OperationId)))
        {
            for (var index = 0; index < snapshot.Length; index++)
            {
                if (!OperationRowProjector.HaveSamePresentation(
                        Operations[index],
                        snapshot[index]))
                {
                    Operations[index] = snapshot[index];
                }
            }

            return;
        }

        Operations.CollectionChanged -= OnOperationsChanged;
        Operations.Clear();
        foreach (var operation in snapshot)
        {
            Operations.Add(operation);
        }
        Operations.CollectionChanged += OnOperationsChanged;
        UpdateState();
    }

    private void OnOperationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        var state = ActivityStateProjector.Project(Operations);
        ActivityList.Visibility = state.HasOperations ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = state.HasOperations ? Visibility.Collapsed : Visibility.Visible;
        ClearHistoryButton.IsEnabled = state.CanClearCompleted;
        ToolTipService.SetToolTip(
            ClearHistoryButton,
            state.HasUnresolvedVerification
                ? "Complete pending verification before clearing activity and diagnostics."
                : "Remove completed activity and app-owned diagnostic logs.");
        CancelQueuedButton.IsEnabled = state.CanCancelQueued;
        QueueSummaryText.Text = state.Summary;
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
                ViewDiagnosticRequested?.Invoke(
                    this,
                    new OperationDiagnosticRequestedEventArgs(operation.OperationId));
            }
        }
    }

    private void OnCancelQueuedClick(object sender, RoutedEventArgs e) => CancelQueuedRequested?.Invoke(this, EventArgs.Empty);
    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (ActivityStateProjector.Project(Operations).CanClearCompleted)
        {
            ClearCompletedRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class OperationDiagnosticRequestedEventArgs(Guid operationId) : EventArgs
{
    public Guid OperationId { get; } = operationId;
}
