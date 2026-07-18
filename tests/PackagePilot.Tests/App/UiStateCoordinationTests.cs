using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using PackagePilot.App.ViewModels;

namespace PackagePilot.Tests.App;

public sealed class UiStateCoordinationTests
{
    [Fact]
    public void KeyedReconciliation_PreservesInstancesAndAppliesMoves()
    {
        var first = new Row("a", "old-a");
        var second = new Row("b", "old-b");
        var rows = new ObservableCollection<Row> { first, second };
        var reconciler = new KeyedCollectionReconciler<string, Row>(StringComparer.Ordinal);

        reconciler.Reconcile(
            rows,
            [new Row("b", "new-b"), new Row("c", "new-c"), new Row("a", "new-a")],
            row => row.Id,
            static (current, replacement) => current.Label = replacement.Label);

        Assert.Same(second, rows[0]);
        Assert.Equal("new-b", rows[0].Label);
        Assert.Equal("c", rows[1].Id);
        Assert.Same(first, rows[2]);
        Assert.Equal("new-a", rows[2].Label);
    }

    [Fact]
    public void KeyedReconciliation_RejectsAmbiguousReplacementKeys()
    {
        var reconciler = new KeyedCollectionReconciler<string, Row>(StringComparer.OrdinalIgnoreCase);

        Assert.Throws<ArgumentException>(() => reconciler.Reconcile(
            [],
            [new Row("a", "one"), new Row("A", "two")],
            row => row.Id));
    }

    [Fact]
    public void HiddenDestination_UpdatesStateWithoutRenderingUntilActivated()
    {
        var scheduled = new Queue<(DispatcherQueuePriority Priority, DispatcherQueueHandler Callback)>();
        var updated = new List<DestinationChangeFlags>();
        var applied = new List<DestinationChangeFlags>();
        var coordinator = new DestinationUiStateCoordinator(
            (priority, callback) =>
            {
                scheduled.Enqueue((priority, callback));
                return true;
            },
            updated.Add,
            applied.Add);

        coordinator.Invalidate(DestinationChangeFlags.Installed);
        Assert.Equal(DispatcherQueuePriority.Low, scheduled.Peek().Priority);
        scheduled.Dequeue().Callback();
        Assert.Equal(DestinationChangeFlags.Installed, Assert.Single(updated));
        Assert.Empty(applied);

        coordinator.Activate(DestinationChangeFlags.Installed);
        Assert.Equal(DispatcherQueuePriority.Normal, scheduled.Peek().Priority);
        scheduled.Dequeue().Callback();

        Assert.Equal(
            DestinationChangeFlags.Installed | DestinationChangeFlags.Shell,
            Assert.Single(applied));
    }

    [Fact]
    public void VisibleAndShellInvalidations_CoalesceIntoOneNormalRender()
    {
        var scheduled = new Queue<(DispatcherQueuePriority Priority, DispatcherQueueHandler Callback)>();
        var applied = new List<DestinationChangeFlags>();
        var coordinator = new DestinationUiStateCoordinator(
            (priority, callback) =>
            {
                scheduled.Enqueue((priority, callback));
                return true;
            },
            applied.Add);

        coordinator.Invalidate(DestinationChangeFlags.Discover);
        coordinator.Invalidate(DestinationChangeFlags.Shell);
        coordinator.Invalidate(DestinationChangeFlags.Activity);

        Assert.Single(scheduled);
        Assert.Equal(DispatcherQueuePriority.Normal, scheduled.Peek().Priority);
        scheduled.Dequeue().Callback();

        Assert.Equal(
            DestinationChangeFlags.Discover | DestinationChangeFlags.Shell,
            Assert.Single(applied));
    }

    private sealed class Row(string id, string label)
    {
        public string Id { get; } = id;
        public string Label { get; set; } = label;
    }
}
