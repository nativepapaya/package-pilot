using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.App;

public sealed class WindowsAppBehaviorSettingsStoreTests
{
    [Fact]
    public async Task MissingOrMalformedValues_LoadSafeDefaults()
    {
        var values = new Dictionary<string, object>
        {
            [WindowsAppBehaviorSettingsStore.ShowNotificationAreaIconKey] = "true",
            [WindowsAppBehaviorSettingsStore.HideOnStartupTaskActivationKey] = 1
        };
        var store = new WindowsAppBehaviorSettingsStore(values);

        AppBehaviorSettings settings = await store.LoadAsync();

        Assert.Equal(new AppBehaviorSettings(), settings);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAllBehaviorSettings()
    {
        var values = new Dictionary<string, object>();
        var store = new WindowsAppBehaviorSettingsStore(values);
        var expected = new AppBehaviorSettings
        {
            ShowNotificationAreaIcon = true,
            HideOnStartupTaskActivation = true,
            MinimizeToNotificationArea = true,
            CloseToNotificationArea = true
        };

        await store.SaveAsync(expected);
        AppBehaviorSettings actual = await store.LoadAsync();

        Assert.Equal(expected, actual);
        Assert.All(values.Values, value => Assert.IsType<bool>(value));
    }

    [Fact]
    public async Task CanceledSave_DoesNotChangeValues()
    {
        var values = new Dictionary<string, object>
        {
            [WindowsAppBehaviorSettingsStore.ShowNotificationAreaIconKey] = false
        };
        var store = new WindowsAppBehaviorSettingsStore(values);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.SaveAsync(
                new AppBehaviorSettings { ShowNotificationAreaIcon = true },
                cancellation.Token));

        Assert.False(Assert.IsType<bool>(
            values[WindowsAppBehaviorSettingsStore.ShowNotificationAreaIconKey]));
    }
}
