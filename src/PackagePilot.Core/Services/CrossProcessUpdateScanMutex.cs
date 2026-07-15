namespace PackagePilot.Core.Services;

/// <summary>
/// Runs update discovery while holding a named Windows mutex. Mutex ownership is thread-affine,
/// so the owning thread blocks on the asynchronous operation and releases the mutex itself.
/// </summary>
public sealed class CrossProcessUpdateScanMutex
{
    public const string DefaultName =
        @"Local\PackagePilot.UpdateDiscovery.5C2B2D42-64E7-47DC-B966-1E408555A39B";

    private readonly string _name;

    public CrossProcessUpdateScanMutex(string? name = null)
    {
        _name = string.IsNullOrWhiteSpace(name) ? DefaultName : name;
    }

    public Task<T> RunExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default,
        TimeSpan? acquisitionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (acquisitionTimeout is { } timeout && timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(acquisitionTimeout));
        }

        // LongRunning gives the kernel mutex a dedicated owner thread. Releasing a Mutex from
        // an arbitrary async continuation is invalid and can leave foreground/background scans
        // racing one another.
        return Task.Factory.StartNew(
            () => RunOnOwnerThread(operation, cancellationToken, acquisitionTimeout),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private T RunOnOwnerThread<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        TimeSpan? acquisitionTimeout)
    {
        using var mutex = new Mutex(initiallyOwned: false, _name);
        var acquired = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (cancellationToken.CanBeCanceled)
                {
                    var signaled = acquisitionTimeout is { } timeout
                        ? WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], timeout)
                        : WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]);
                    if (signaled == WaitHandle.WaitTimeout)
                    {
                        throw new CrossProcessUpdateScanBusyException();
                    }

                    if (signaled != 0)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
                else if (acquisitionTimeout is { } timeout)
                {
                    if (!mutex.WaitOne(timeout))
                    {
                        throw new CrossProcessUpdateScanBusyException();
                    }
                }
                else
                {
                    mutex.WaitOne();
                }

                acquired = true;
            }
            catch (AbandonedMutexException)
            {
                // The previous process ended without releasing the mutex. Windows transfers
                // ownership to this thread, so the snapshot can be safely recovered/replaced.
                acquired = true;
            }

            return operation(cancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}

/// <summary>An automatic scan could not acquire the shared scan mutex within its time budget.</summary>
public sealed class CrossProcessUpdateScanBusyException : TimeoutException
{
    public CrossProcessUpdateScanBusyException()
        : base("Another update check is already running.")
    {
    }
}
