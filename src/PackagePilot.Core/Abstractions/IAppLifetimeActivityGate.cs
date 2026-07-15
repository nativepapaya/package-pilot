using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Serializes exit-sensitive foreground work and closes the activity boundary atomically with
/// the package queue when orderly shutdown is accepted.
/// </summary>
public interface IAppLifetimeActivityGate
{
    AppLifetimeActivitySnapshot Snapshot { get; }

    /// <summary>
    /// Starts an activity when no other source activity is running and shutdown has not been
    /// committed. The returned lease must be disposed after all related work has completed.
    /// </summary>
    IDisposable? TryEnter(AppLifetimeActivityKind kind);

    /// <summary>
    /// Commits shutdown only when the source boundary is idle and the supplied package-queue
    /// commit succeeds. The callback runs while new source work is excluded.
    /// </summary>
    bool TryBeginShutdownIfIdle(
        Func<bool> tryCommitPackageShutdown,
        out AppLifetimeActivityKind? blockingActivity);

    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}
