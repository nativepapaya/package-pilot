using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>A lock-based activity gate shared by the foreground graph and app lifetime owner.</summary>
public sealed class AppLifetimeActivityGate : IAppLifetimeActivityGate
{
    private readonly object _sync = new();
    private AppLifetimeActivityKind? _activeKind;
    private TaskCompletionSource? _idleCompletion;
    private bool _shutdownCommitted;

    public AppLifetimeActivitySnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new AppLifetimeActivitySnapshot
                {
                    ActiveKind = _activeKind,
                    IsShutdownCommitted = _shutdownCommitted
                };
            }
        }
    }

    public IDisposable? TryEnter(AppLifetimeActivityKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        lock (_sync)
        {
            if (_shutdownCommitted || _activeKind is not null)
            {
                return null;
            }

            _activeKind = kind;
            _idleCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return new ActivityLease(this, kind);
        }
    }

    public bool TryBeginShutdownIfIdle(
        Func<bool> tryCommitPackageShutdown,
        out AppLifetimeActivityKind? blockingActivity)
    {
        ArgumentNullException.ThrowIfNull(tryCommitPackageShutdown);

        lock (_sync)
        {
            blockingActivity = _activeKind;
            if (_activeKind is not null)
            {
                return false;
            }

            if (_shutdownCommitted)
            {
                return true;
            }

            if (!tryCommitPackageShutdown())
            {
                return false;
            }

            _shutdownCommitted = true;
            return true;
        }
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idleTask;
        lock (_sync)
        {
            idleTask = _idleCompletion?.Task ?? Task.CompletedTask;
        }

        return cancellationToken.CanBeCanceled
            ? idleTask.WaitAsync(cancellationToken)
            : idleTask;
    }

    private void Exit(AppLifetimeActivityKind kind)
    {
        TaskCompletionSource? idleCompletion;
        lock (_sync)
        {
            if (_activeKind != kind)
            {
                return;
            }

            _activeKind = null;
            idleCompletion = _idleCompletion;
            _idleCompletion = null;
        }

        idleCompletion?.TrySetResult();
    }

    private sealed class ActivityLease(
        AppLifetimeActivityGate owner,
        AppLifetimeActivityKind kind) : IDisposable
    {
        private AppLifetimeActivityGate? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Exit(kind);
    }
}
