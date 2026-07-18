using PackagePilot.App.ViewModels;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.App;

public sealed class ShellActivityViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActiveOperation_ProjectsExactProgressAndQueueSummary()
    {
        var viewModel = new ShellActivityViewModel();
        var current = Entry("Contoso.App", PackageOperationState.Downloading, 42);
        var pending = Entry("Fabrikam.App", PackageOperationState.Queued, null);

        var state = viewModel.Project(
            new OperationQueueSnapshot { Current = current, Pending = [pending] },
            [],
            Now);

        Assert.True(state.IsVisible);
        Assert.Contains("Contoso.App", state.Summary);
        Assert.Equal("1 operation queued next", state.Detail);
        Assert.Equal(42, state.Progress);
        Assert.False(state.IsIndeterminate);
    }

    [Fact]
    public void SuccessfulCompletion_CollapsesAfterFourSeconds()
    {
        var viewModel = new ShellActivityViewModel();
        var result = Result(PackageOperationState.Completed, Now);
        var queue = new OperationQueueSnapshot { History = [result] };

        Assert.True(viewModel.Project(queue, [], Now.AddSeconds(3)).IsVisible);
        Assert.False(viewModel.Project(queue, [], Now.AddSeconds(4)).IsVisible);
    }

    [Fact]
    public void Failure_RemainsUntilActivityIsReviewed()
    {
        var viewModel = new ShellActivityViewModel();
        var result = Result(PackageOperationState.Failed, Now) with
        {
            Error = new WingetError { Message = "Installer failed" }
        };
        var queue = new OperationQueueSnapshot { History = [result] };

        Assert.True(viewModel.Project(queue, [], Now.AddHours(2)).IsPersistent);
        viewModel.MarkReviewed(queue, []);
        Assert.False(viewModel.Project(queue, [], Now.AddHours(2)).IsVisible);
    }

    [Fact]
    public void UnresolvedVerification_RemainsUntilReviewed()
    {
        var viewModel = new ShellActivityViewModel();
        var marker = new MutationVerificationMarker
        {
            OperationId = Guid.NewGuid(),
            Package = new PackageSummary { Name = "Contoso App" },
            RecordedAt = Now,
            Phase = MutationVerificationPhase.ApplicationRestartPending
        };

        var state = viewModel.Project(new OperationQueueSnapshot(), [marker], Now.AddDays(1));

        Assert.True(state.IsPersistent);
        Assert.Equal(ShellActivitySeverity.Warning, state.Severity);
    }

    private static OperationQueueEntry Entry(
        string name,
        PackageOperationState state,
        double? percent) => new(
        new PackageOperation
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            Package = new PackageKey(name, "source")
        },
        new OperationProgress
        {
            State = state,
            Percent = percent,
            Timestamp = Now
        });

    private static OperationResult Result(PackageOperationState state, DateTimeOffset completed) => new()
    {
        OperationId = Guid.NewGuid(),
        Package = new PackageKey("Contoso.App", "source"),
        Kind = PackageOperationKind.Upgrade,
        State = state,
        CompletedAt = completed
    };
}
