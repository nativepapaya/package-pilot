using PackagePilot.Core.Models;

namespace PackagePilot.Tests.Core;

public sealed class AppBehaviorModelsTests
{
    [Fact]
    public void Defaults_DoNotCreateResidentOrNotificationAreaBehavior()
    {
        var settings = new AppBehaviorSettings();

        Assert.False(settings.ShowNotificationAreaIcon);
        Assert.False(settings.HideOnStartupTaskActivation);
        Assert.False(settings.MinimizeToNotificationArea);
        Assert.False(settings.CloseToNotificationArea);
        Assert.False(settings.UsesNotificationAreaIcon);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void AnyResidentBehavior_RequiresNotificationAreaIcon(
        bool show,
        bool hideOnStartup,
        bool minimize,
        bool close)
    {
        var settings = new AppBehaviorSettings
        {
            ShowNotificationAreaIcon = show,
            HideOnStartupTaskActivation = hideOnStartup,
            MinimizeToNotificationArea = minimize,
            CloseToNotificationArea = close
        };

        Assert.True(settings.UsesNotificationAreaIcon);
    }

    [Theory]
    [InlineData(StartupRegistrationState.Disabled, false, true, false, false, false)]
    [InlineData(StartupRegistrationState.Enabled, true, false, true, false, false)]
    [InlineData(StartupRegistrationState.DisabledByUser, false, false, false, true, false)]
    [InlineData(StartupRegistrationState.DisabledByPolicy, false, false, false, false, true)]
    [InlineData(StartupRegistrationState.EnabledByPolicy, true, false, false, false, true)]
    public void StartupResult_ExposesSafeUserActions(
        StartupRegistrationState state,
        bool enabled,
        bool canEnable,
        bool canDisable,
        bool requiresSettings,
        bool policyManaged)
    {
        var result = new StartupRegistrationResult { State = state };

        Assert.Equal(enabled, result.IsEnabled);
        Assert.Equal(canEnable, result.CanEnable);
        Assert.Equal(canDisable, result.CanDisable);
        Assert.Equal(requiresSettings, result.RequiresWindowsSettings);
        Assert.Equal(policyManaged, result.IsPolicyManaged);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void WindowActivity_RequiresVisibleAndActive(
        bool visible,
        bool active,
        bool expectedForeground)
    {
        Assert.Equal(
            expectedForeground,
            new WindowActivityState(visible, active).IsForegroundActive);
    }
}
