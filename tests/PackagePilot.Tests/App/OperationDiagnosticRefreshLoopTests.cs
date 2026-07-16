using PackagePilot.App;

namespace PackagePilot.Tests.App;

public sealed class OperationDiagnosticRefreshLoopTests
{
    [Fact]
    public async Task Run_StopsWithoutAnotherDelayWhenOperationCompletes()
    {
        var refreshes = 0;
        var delays = 0;
        var loop = Loop(
            maximumRefreshes: 5,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        var result = await loop.RunAsync(
            _ => Task.FromResult(++refreshes < 2),
            CancellationToken.None);

        Assert.Equal(OperationDiagnosticRefreshLoopResult.OperationCompleted, result);
        Assert.Equal(2, refreshes);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task Run_StopsAtTheHardRefreshLimit()
    {
        var refreshes = 0;
        var delays = 0;
        var loop = Loop(
            maximumRefreshes: 3,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        var result = await loop.RunAsync(
            _ =>
            {
                refreshes++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(OperationDiagnosticRefreshLoopResult.LimitReached, result);
        Assert.Equal(3, refreshes);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task Run_CancelsAWaitingRefreshWithoutAnotherRead()
    {
        using var cancellation = new CancellationTokenSource();
        var firstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0;
        var loop = Loop(
            maximumRefreshes: 5,
            static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        var running = loop.RunAsync(
            _ =>
            {
                refreshes++;
                firstRead.TrySetResult();
                return Task.FromResult(true);
            },
            cancellation.Token);
        await firstRead.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, refreshes);
    }

    [Theory]
    [InlineData(true, true, 0, 0, 0, 120, 120)]
    [InlineData(true, false, 0, 0, 0, 120, 0)]
    [InlineData(false, true, 20, 0, 100, 140, 20)]
    [InlineData(false, true, 100, 0, 100, 140, 140)]
    public void UpdatedSelection_LiveStartsAtTailAndThenPreservesReadingPosition(
        bool firstDisplay,
        bool isLive,
        int priorStart,
        int priorLength,
        int priorTextLength,
        int updatedTextLength,
        int expected)
    {
        var actual = OperationDiagnosticRefreshLoop.GetUpdatedSelectionStart(
            firstDisplay,
            isLive,
            priorStart,
            priorLength,
            priorTextLength,
            updatedTextLength);

        Assert.Equal(expected, actual);
    }

    private static OperationDiagnosticRefreshLoop Loop(
        int maximumRefreshes,
        Func<TimeSpan, CancellationToken, Task> delayAsync) =>
        new(maximumRefreshes, TimeSpan.FromSeconds(1), delayAsync);
}
