using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;
using Windows.ApplicationModel;

namespace PackagePilot.Tests.App;

public sealed class WindowsStartupAndNotificationAreaTests
{
    [Theory]
    [InlineData(StartupTaskState.Disabled, StartupRegistrationState.Disabled)]
    [InlineData(StartupTaskState.Enabled, StartupRegistrationState.Enabled)]
    [InlineData(StartupTaskState.DisabledByUser, StartupRegistrationState.DisabledByUser)]
    [InlineData(StartupTaskState.DisabledByPolicy, StartupRegistrationState.DisabledByPolicy)]
    [InlineData(StartupTaskState.EnabledByPolicy, StartupRegistrationState.EnabledByPolicy)]
    public void StartupTaskState_MappingPreservesWindowsAuthority(
        StartupTaskState state,
        StartupRegistrationState expected)
    {
        Assert.Equal(expected, WindowsStartupRegistrationService.MapState(state));
    }

    [Fact]
    public void StartupActivation_AcceptsOnlyManifestTaskId()
    {
        var accepted = WindowsActivationAdapter.ParseStartupTask(
            WindowsIntegrationConstants.StartupTaskId);
        var rejected = WindowsActivationAdapter.ParseStartupTask("Contoso.Startup");

        Assert.True(accepted.IsAccepted);
        Assert.True(accepted.Request!.IsStartupTaskActivation);
        Assert.False(rejected.IsAccepted);
    }

    [Fact]
    public void NotificationAreaIdentity_IsStable()
    {
        Assert.Equal(
            new Guid("AA7F3952-947C-4577-A1B7-DEA47398CCBC"),
            WindowsIntegrationConstants.NotificationAreaIconId);
    }

    [Fact]
    public void NotificationAreaText_SanitizesCountsAndNativeTooltipLimit()
    {
        string toolTip = WindowsNotificationAreaIconService.BuildToolTip(new NotificationAreaIconOptions
        {
            ToolTip = new string('a', 200),
            UpdateCount = -10
        });

        Assert.Equal(127, toolTip.Length);
        Assert.Equal("Review updates (0)", WindowsNotificationAreaIconService.BuildReviewUpdatesLabel(-1));
        Assert.Equal("Review updates (3)", WindowsNotificationAreaIconService.BuildReviewUpdatesLabel(3));
    }

    [Theory]
    [InlineData(1001, NotificationAreaAction.Open)]
    [InlineData(1002, NotificationAreaAction.ReviewUpdates)]
    [InlineData(1003, NotificationAreaAction.CheckNow)]
    [InlineData(1004, NotificationAreaAction.OpenSettings)]
    [InlineData(1005, NotificationAreaAction.Exit)]
    public void ContextMenuCommands_AreAllowlisted(uint command, NotificationAreaAction expected)
    {
        Assert.Equal(expected, WindowsNotificationAreaIconService.MapMenuCommand(command));
    }

    [Fact]
    public void ContextMenuCommands_RejectUnknownIds()
    {
        Assert.Null(WindowsNotificationAreaIconService.MapMenuCommand(9999));
    }

    [Theory]
    [InlineData(NotificationAreaAvailabilityReason.ExplorerRestartRecovered)]
    [InlineData(NotificationAreaAvailabilityReason.ExplorerRestartFailed)]
    [InlineData(NotificationAreaAvailabilityReason.UpdateFailed)]
    public void AvailabilityChangedEvent_PreservesNativeResultAndReason(
        NotificationAreaAvailabilityReason reason)
    {
        var result = new NotificationAreaIconResult
        {
            State = reason == NotificationAreaAvailabilityReason.ExplorerRestartRecovered
                ? NotificationAreaIconState.Visible
                : NotificationAreaIconState.Failed,
            Message = "Explorer state changed."
        };

        var args = new NotificationAreaAvailabilityChangedEventArgs(result, reason);

        Assert.Same(result, args.Result);
        Assert.Equal(reason, args.Reason);
    }

    [Fact]
    public void AvailabilityChangedEvent_RejectsMissingNativeResult()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NotificationAreaAvailabilityChangedEventArgs(
                null!,
                NotificationAreaAvailabilityReason.UpdateFailed));
    }
}
