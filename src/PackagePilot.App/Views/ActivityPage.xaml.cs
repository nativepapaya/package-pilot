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
    public event EventHandler? CancelQueuedRequested;
    public event EventHandler? ClearCompletedRequested;

    public void SetOperations(IEnumerable<OperationListItem> operations)
    {
        Operations.CollectionChanged -= OnOperationsChanged;
        Operations.Clear();
        foreach (var operation in operations)
        {
            Operations.Add(operation);
        }
        Operations.CollectionChanged += OnOperationsChanged;
        UpdateState();
    }

    private void OnOperationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        var hasOperations = Operations.Count > 0;
        var activeCount = Operations.Count(operation => operation.IsActive);
        var cancellableCount = Operations.Count(operation => operation.CanCancel);
        ActivityList.Visibility = hasOperations ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = hasOperations ? Visibility.Collapsed : Visibility.Visible;
        ClearHistoryButton.IsEnabled = Operations.Any(operation => !operation.IsActive);
        CancelQueuedButton.IsEnabled = cancellableCount > 0;
        QueueSummaryText.Text = activeCount switch
        {
            0 when cancellableCount == 0 => "Queue is idle",
            0 => $"{cancellableCount} operation{(cancellableCount == 1 ? string.Empty : "s")} queued",
            1 => "1 operation in progress",
            _ => $"{activeCount} operations active"
        };
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

    private void OnCancelQueuedClick(object sender, RoutedEventArgs e) => CancelQueuedRequested?.Invoke(this, EventArgs.Empty);
    private void OnClearHistoryClick(object sender, RoutedEventArgs e) => ClearCompletedRequested?.Invoke(this, EventArgs.Empty);
}
