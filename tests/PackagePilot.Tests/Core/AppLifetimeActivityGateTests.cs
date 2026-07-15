using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class AppLifetimeActivityGateTests
{
    [Fact]
    public void TryEnter_SerializesSourceAndIntegrationWork()
    {
        var gate = new AppLifetimeActivityGate();

        using var first = gate.TryEnter(AppLifetimeActivityKind.SourceRefresh);

        Assert.NotNull(first);
        Assert.Null(gate.TryEnter(AppLifetimeActivityKind.SourceMutation));
        Assert.Null(gate.TryEnter(AppLifetimeActivityKind.WindowsIntegration));
        Assert.Equal(AppLifetimeActivityKind.SourceRefresh, gate.Snapshot.ActiveKind);

        first.Dispose();
        using var next = gate.TryEnter(AppLifetimeActivityKind.SourceMutation);
        Assert.NotNull(next);
    }

    [Fact]
    public void TryBeginShutdownIfIdle_ReportsSourceBlockerWithoutClosingPackageQueue()
    {
        var gate = new AppLifetimeActivityGate();
        using var activity = gate.TryEnter(AppLifetimeActivityKind.SourceMutation);
        var packageCommitCalls = 0;

        bool result = gate.TryBeginShutdownIfIdle(
            () =>
            {
                packageCommitCalls++;
                return true;
            },
            out var blocker);

        Assert.False(result);
        Assert.Equal(AppLifetimeActivityKind.SourceMutation, blocker);
        Assert.Equal(0, packageCommitCalls);
        Assert.False(gate.Snapshot.IsShutdownCommitted);
    }

    [Fact]
    public void TryBeginShutdownIfIdle_FailedPackageCommitLeavesActivityGateOpen()
    {
        var gate = new AppLifetimeActivityGate();

        Assert.False(gate.TryBeginShutdownIfIdle(() => false, out var blocker));
        Assert.Null(blocker);
        Assert.False(gate.Snapshot.IsShutdownCommitted);

        using var activity = gate.TryEnter(AppLifetimeActivityKind.SourceRefresh);
        Assert.NotNull(activity);
    }

    [Fact]
    public void TryBeginShutdownIfIdle_CommitsBothBoundariesAndRejectsNewWork()
    {
        var gate = new AppLifetimeActivityGate();
        var packageCommitted = false;

        Assert.True(gate.TryBeginShutdownIfIdle(
            () => packageCommitted = true,
            out var blocker));

        Assert.True(packageCommitted);
        Assert.Null(blocker);
        Assert.True(gate.Snapshot.IsShutdownCommitted);
        Assert.Null(gate.TryEnter(AppLifetimeActivityKind.SourceRefresh));
        Assert.True(gate.TryBeginShutdownIfIdle(() => throw new InvalidOperationException(), out _));
    }

    [Fact]
    public void EnterAndShutdownRace_HasExactlyOneWinner()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var gate = new AppLifetimeActivityGate();
            IDisposable? activity = null;
            var shutdownAccepted = false;

            Parallel.Invoke(
                () => activity = gate.TryEnter(AppLifetimeActivityKind.SourceRefresh),
                () => shutdownAccepted = gate.TryBeginShutdownIfIdle(() => true, out _));

            Assert.NotEqual(activity is not null, shutdownAccepted);
            activity?.Dispose();
        }
    }

    [Fact]
    public async Task WaitForIdleAsync_CompletesOnlyAfterLeaseIsReleased()
    {
        var gate = new AppLifetimeActivityGate();
        var activity = gate.TryEnter(AppLifetimeActivityKind.WindowsIntegration);
        Assert.NotNull(activity);

        var idle = gate.WaitForIdleAsync();
        Assert.False(idle.IsCompleted);

        activity.Dispose();
        await idle.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(gate.Snapshot.ActiveKind);
    }
}
