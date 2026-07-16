namespace PackagePilot.Windows.Services;

internal interface IMsixRemovalCompletionMonitor
{
    Task<Guid> WaitForSuccessfulRemovalAsync(
        string packageFullName,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Recovers the exact successful Windows deployment Activity ID when the WinRT deployment
/// operation finishes in Windows but its managed completion callback never arrives.
/// </summary>
internal sealed class WindowsMsixRemovalCompletionMonitor : IMsixRemovalCompletionMonitor
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<Guid> WaitForSuccessfulRemovalAsync(
        string packageFullName,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        await Task.Delay(InitialDelay, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var activityId = await WindowsDeploymentEventLogReader
                    .FindSuccessfulRemovalAsync(packageFullName, startedAt, cancellationToken)
                    .ConfigureAwait(false);
                if (activityId is { } exactActivityId && exactActivityId != Guid.Empty)
                {
                    return exactActivityId;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // Event-log access is a recovery path, not permission to fail or release an active
                // mutation. Keep waiting for either a later exact event or the native WinRT result.
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal static class MsixRemovalCompletionCoordinator
{
    public static async Task<MsixRemovalCompletion<T>> WaitAsync<T>(
        Task<T> nativeCompletion,
        Task<Guid> recoveredCompletion,
        CancellationTokenSource recoveryCancellation)
    {
        ArgumentNullException.ThrowIfNull(nativeCompletion);
        ArgumentNullException.ThrowIfNull(recoveredCompletion);
        ArgumentNullException.ThrowIfNull(recoveryCancellation);

        _ = await Task.WhenAny(nativeCompletion, recoveredCompletion).ConfigureAwait(false);
        if (nativeCompletion.IsCompleted)
        {
            recoveryCancellation.Cancel();
            ObserveFault(recoveredCompletion);
            return MsixRemovalCompletion<T>.Native(
                await nativeCompletion.ConfigureAwait(false));
        }

        try
        {
            var activityId = await recoveredCompletion.ConfigureAwait(false);
            if (activityId != Guid.Empty)
            {
                ObserveFault(nativeCompletion);
                return MsixRemovalCompletion<T>.Recovered(activityId);
            }
        }
        catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
        {
            // The native result won the race while the recovery query was being cancelled.
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Recovery failure must never release the mutation queue. Fall back to Windows' native
            // completion even if it takes longer.
        }

        return MsixRemovalCompletion<T>.Native(
            await nativeCompletion.ConfigureAwait(false));
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal readonly record struct MsixRemovalCompletion<T>(
    bool WasRecovered,
    T NativeResult,
    Guid ActivityId)
{
    public static MsixRemovalCompletion<T> Native(T result) =>
        new(false, result, Guid.Empty);

    public static MsixRemovalCompletion<T> Recovered(Guid activityId) =>
        new(true, default!, activityId);
}
