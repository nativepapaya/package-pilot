namespace PackagePilot.App;

internal enum OperationDiagnosticRefreshLoopResult
{
    OperationCompleted,
    LimitReached
}

/// <summary>
/// Runs bounded, serialized foreground refreshes. The loop exists only for an open diagnostic
/// dialog; it does not watch files and cancellation stops the next delay or read immediately.
/// </summary>
internal sealed class OperationDiagnosticRefreshLoop
{
    internal const int MaximumAutomaticRefreshes = 60;
    internal static readonly TimeSpan AutomaticRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly int _maximumRefreshes;
    private readonly TimeSpan _refreshInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public OperationDiagnosticRefreshLoop()
        : this(
            MaximumAutomaticRefreshes,
            AutomaticRefreshInterval,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal OperationDiagnosticRefreshLoop(
        int maximumRefreshes,
        TimeSpan refreshInterval,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRefreshes, 1);
        if (refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }

        _maximumRefreshes = maximumRefreshes;
        _refreshInterval = refreshInterval;
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public async Task<OperationDiagnosticRefreshLoopResult> RunAsync(
        Func<CancellationToken, Task<bool>> refreshAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refreshAsync);

        for (var refreshCount = 0; refreshCount < _maximumRefreshes; refreshCount++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await refreshAsync(cancellationToken))
            {
                return OperationDiagnosticRefreshLoopResult.OperationCompleted;
            }

            if (refreshCount + 1 >= _maximumRefreshes)
            {
                return OperationDiagnosticRefreshLoopResult.LimitReached;
            }

            // Preserve the caller's UI context. The next callback updates the open dialog.
            await _delayAsync(_refreshInterval, cancellationToken);
        }

        return OperationDiagnosticRefreshLoopResult.LimitReached;
    }

    internal static int GetUpdatedSelectionStart(
        bool firstDisplay,
        bool isLive,
        int priorSelectionStart,
        int priorSelectionLength,
        int priorTextLength,
        int updatedTextLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priorSelectionStart);
        ArgumentOutOfRangeException.ThrowIfNegative(priorSelectionLength);
        ArgumentOutOfRangeException.ThrowIfNegative(priorTextLength);
        ArgumentOutOfRangeException.ThrowIfNegative(updatedTextLength);

        if (firstDisplay)
        {
            return isLive ? updatedTextLength : 0;
        }

        var wasAtEnd = (long)priorSelectionStart + priorSelectionLength >= priorTextLength;
        return wasAtEnd
            ? updatedTextLength
            : Math.Min(priorSelectionStart, updatedTextLength);
    }
}
