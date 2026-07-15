using PackagePilot.Core.Models;

namespace PackagePilot.Tests.Core;

public sealed class WindowsIntegrationCapabilityTests
{
    [Theory]
    [InlineData(WingetCapabilities.PackageRepairContractVersion - 1, false)]
    [InlineData(WingetCapabilities.PackageRepairContractVersion, true)]
    [InlineData(WingetCapabilities.PackageRepairContractVersion + 1, true)]
    public void PackageRepair_IsReportedOnlyForSupportedContract(
        uint contractVersion,
        bool expected)
    {
        var capabilities = new WingetCapabilities
        {
            IsAvailable = true,
            ContractVersion = contractVersion
        };

        Assert.Equal(expected, capabilities.SupportsPackageRepair);
    }

    [Fact]
    public void WindowsCapabilityReport_KeepsIdentityBoundFeaturesIndependent()
    {
        var capabilities = new WindowsIntegrationCapabilities
        {
            SupportsPackageRepair = true,
            SupportsSourceRefresh = true,
            SupportsSourceMutation = false,
            SupportsSourceExplicitEdit = false,
            BackgroundMonitoringState = BackgroundMonitoringState.Denied,
            NotificationRegistrationSupported = true,
            NotificationRegistered = false
        };

        Assert.True(capabilities.SupportsPackageRepair);
        Assert.True(capabilities.SupportsSourceRefresh);
        Assert.False(capabilities.SupportsSourceMutation);
        Assert.Equal(BackgroundMonitoringState.Denied, capabilities.BackgroundMonitoringState);
        Assert.True(capabilities.NotificationRegistrationSupported);
        Assert.False(capabilities.NotificationRegistered);
    }
}
