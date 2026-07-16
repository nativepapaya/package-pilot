using PackagePilot.App.Views;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.App;

public sealed class ActivityStateProjectorTests
{
    [Fact]
    public void VerificationPending_IsNotReportedAsIdleAndCannotBeCleared()
    {
        var state = ActivityStateProjector.Project(
        [
            PendingHistory(MutationVerificationPhase.VerificationPending),
            CompletedHistory()
        ]);

        Assert.Equal("1 operation awaiting verification", state.Summary);
        Assert.True(state.HasUnresolvedVerification);
        Assert.False(state.CanClearCompleted);
    }

    [Fact]
    public void ApplicationRestartPending_IsNotReportedAsIdleAndCannotBeCleared()
    {
        var state = ActivityStateProjector.Project(
        [
            PendingHistory(MutationVerificationPhase.ApplicationRestartPending)
        ]);

        Assert.Equal("1 operation awaiting an app restart", state.Summary);
        Assert.True(state.HasUnresolvedVerification);
        Assert.False(state.CanClearCompleted);
    }

    [Fact]
    public void ResolvedHistory_LeavesQueueIdleAndCanBeCleared()
    {
        var state = ActivityStateProjector.Project([CompletedHistory()]);

        Assert.Equal("Queue is idle", state.Summary);
        Assert.False(state.HasUnresolvedVerification);
        Assert.True(state.CanClearCompleted);
    }

    [Fact]
    public void QueueAndVerificationStates_AreAllRepresentedInSummary()
    {
        var state = ActivityStateProjector.Project(
        [
            new OperationListItem { IsActive = true },
            new OperationListItem { CanCancel = true },
            PendingHistory(MutationVerificationPhase.VerificationPending),
            PendingHistory(MutationVerificationPhase.ApplicationRestartPending)
        ]);

        Assert.Equal(
            "1 operation in progress, 1 operation queued, 1 operation awaiting verification, 1 operation awaiting an app restart",
            state.Summary);
        Assert.True(state.CanCancelQueued);
    }

    private static OperationListItem PendingHistory(MutationVerificationPhase phase) => new()
    {
        IsHistory = true,
        IsVerificationPending = true,
        VerificationPhase = phase
    };

    private static OperationListItem CompletedHistory() => new()
    {
        IsHistory = true
    };
}
