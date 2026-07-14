using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

public interface IOperationQueue : IAsyncDisposable
{
    event EventHandler<OperationQueueChangedEventArgs>? Changed;

    /// <summary>Completes after retained history has been loaded.</summary>
    Task Initialization { get; }

    OperationQueueSnapshot Snapshot { get; }

    Guid Enqueue(PackageOperation operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation while an operation is queued, resolving, or downloading. Returns
    /// false after installer execution has begun or when the operation is no longer active.
    /// </summary>
    bool TryCancel(Guid operationId);

    /// <summary>Clears retained completed results without affecting active or queued operations.</summary>
    void ClearHistory();

    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}

public interface IOperationHistoryStore
{
    Task<IReadOnlyList<OperationResult>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyList<OperationResult> results,
        CancellationToken cancellationToken = default);
}
