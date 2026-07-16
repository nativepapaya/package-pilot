using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Windows;

public sealed class WindowsMsixRemovalCompletionTests
{
    private const string PackageFullName =
        "Claude_1.21459.3.0_x64__pzs8sxrjxfjjc";

    [Fact]
    public void Query_UsesExactSuccessfulRemovalCallerPackageAndStartTime()
    {
        var startedAt = DateTimeOffset.Parse(
            "2026-07-16T20:40:08.9084740+00:00",
            System.Globalization.CultureInfo.InvariantCulture);

        var query = AppxDeploymentRemovalCompletionQuery.Create(PackageFullName, startedAt);

        Assert.Equal(
            "*[System[(EventID=400) and " +
            "TimeCreated[@SystemTime >= '2026-07-16T20:40:08.9084740Z']] and " +
            "EventData[Data[@Name='DeploymentOperation']='2' and " +
            $"Data[@Name='PackageFullName']='{PackageFullName}' and " +
            "Data[@Name='CallingProcess']='PackagePilot.App.exe']]",
            query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Claude' or EventID=400")]
    [InlineData("Claude/unsafe")]
    public void Query_RejectsMissingOrUnsafePackageNames(string packageFullName)
    {
        Assert.Throws<ArgumentException>(() =>
            AppxDeploymentRemovalCompletionQuery.Create(
                packageFullName,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EventParser_ReturnsOnlyTheExactSuccessfulRemovalActivity()
    {
        var activityId = Guid.Parse("a9dca36c-1507-0013-2ac3-bbaf0715dd01");
        var xml = CreateEventXml(activityId, PackageFullName, "PackagePilot.App.exe", "2");

        Assert.Equal(
            activityId,
            WindowsDeploymentEventLogReader.TryGetSuccessfulRemovalActivityId(
                xml,
                PackageFullName));
        Assert.Null(WindowsDeploymentEventLogReader.TryGetSuccessfulRemovalActivityId(
            xml,
            "Different_1.0.0.0_x64__publisher"));
        Assert.Null(WindowsDeploymentEventLogReader.TryGetSuccessfulRemovalActivityId(
            CreateEventXml(activityId, PackageFullName, "Other.App.exe", "2"),
            PackageFullName));
        Assert.Null(WindowsDeploymentEventLogReader.TryGetSuccessfulRemovalActivityId(
            CreateEventXml(activityId, PackageFullName, "PackagePilot.App.exe", "1"),
            PackageFullName));
    }

    [Fact]
    public async Task Coordinator_UsesExactRecoveredActivityWhenNativeCompletionStalls()
    {
        var native = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activityId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        var result = await MsixRemovalCompletionCoordinator.WaitAsync(
            native.Task,
            Task.FromResult(activityId),
            cancellation);

        Assert.True(result.WasRecovered);
        Assert.Equal(activityId, result.ActivityId);
        Assert.Null(result.NativeResult);
        native.SetResult("late native result");
    }

    [Fact]
    public async Task Coordinator_PrefersNativeCompletionAndCancelsRecoveryQuery()
    {
        var recovered = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var result = await MsixRemovalCompletionCoordinator.WaitAsync(
            Task.FromResult("native result"),
            recovered.Task,
            cancellation);

        Assert.False(result.WasRecovered);
        Assert.Equal("native result", result.NativeResult);
        Assert.Equal(Guid.Empty, result.ActivityId);
        Assert.True(cancellation.IsCancellationRequested);
        recovered.TrySetCanceled(cancellation.Token);
    }

    [Fact]
    public async Task Coordinator_NeverReleasesQueueForRecoveryFailureAlone()
    {
        var native = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var completion = MsixRemovalCompletionCoordinator.WaitAsync(
            native.Task,
            Task.FromException<Guid>(new InvalidOperationException("Event log unavailable.")),
            cancellation);

        await Task.Yield();
        Assert.False(completion.IsCompleted);
        native.SetResult("native result");

        var result = await completion;
        Assert.False(result.WasRecovered);
        Assert.Equal("native result", result.NativeResult);
    }

    private static string CreateEventXml(
        Guid activityId,
        string packageFullName,
        string callingProcess,
        string deploymentOperation) =>
        $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <EventID>400</EventID>
            <Correlation ActivityID="{activityId}" />
          </System>
          <EventData>
            <Data Name="DeploymentOperation">{deploymentOperation}</Data>
            <Data Name="PackageFullName">{packageFullName}</Data>
            <Data Name="CallingProcess">{callingProcess}</Data>
          </EventData>
        </Event>
        """;
}
