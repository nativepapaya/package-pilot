using PackagePilot.Core.Services;

namespace PackagePilot.App.Views;

internal static class ActivityStateProjector
{
    public static ActivityPageState Project(IEnumerable<OperationListItem> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var snapshot = operations as IReadOnlyCollection<OperationListItem>
            ?? operations.ToArray();
        var activeCount = snapshot.Count(operation => operation.IsActive);
        var queuedCount = snapshot.Count(operation =>
            !operation.IsHistory && !operation.IsActive);
        var cancellableQueuedCount = snapshot.Count(operation =>
            !operation.IsHistory && !operation.IsActive && operation.CanCancel);
        var unresolvedCount = snapshot.Count(operation => operation.IsVerificationPending);
        var appRestartCount = snapshot.Count(operation =>
            operation.IsVerificationPending
            && operation.VerificationPhase == MutationVerificationPhase.ApplicationRestartPending);
        var verificationCount = unresolvedCount - appRestartCount;
        var clearableHistoryCount = snapshot.Count(operation =>
            operation.IsHistory && !operation.IsVerificationPending);

        var summaryParts = new List<string>(4);
        AddCount(
            summaryParts,
            activeCount,
            "1 operation in progress",
            count => $"{count} operations active");
        AddCount(
            summaryParts,
            queuedCount,
            "1 operation queued",
            count => $"{count} operations queued");
        AddCount(
            summaryParts,
            verificationCount,
            "1 operation awaiting verification",
            count => $"{count} operations awaiting verification");
        AddCount(
            summaryParts,
            appRestartCount,
            "1 operation awaiting an app restart",
            count => $"{count} operations awaiting an app restart");

        return new ActivityPageState
        {
            HasOperations = snapshot.Count > 0,
            CanCancelQueued = cancellableQueuedCount > 0,
            CanClearCompleted = clearableHistoryCount > 0 && unresolvedCount == 0,
            HasUnresolvedVerification = unresolvedCount > 0,
            Summary = summaryParts.Count == 0
                ? "Queue is idle"
                : string.Join(", ", summaryParts)
        };
    }

    private static void AddCount(
        ICollection<string> parts,
        int count,
        string singular,
        Func<int, string> plural)
    {
        if (count == 1)
        {
            parts.Add(singular);
        }
        else if (count > 1)
        {
            parts.Add(plural(count));
        }
    }
}

internal sealed record ActivityPageState
{
    public bool HasOperations { get; init; }
    public bool CanCancelQueued { get; init; }
    public bool CanClearCompleted { get; init; }
    public bool HasUnresolvedVerification { get; init; }
    public string Summary { get; init; } = "Queue is idle";
}
