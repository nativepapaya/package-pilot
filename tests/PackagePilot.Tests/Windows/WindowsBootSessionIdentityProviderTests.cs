using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Windows;

public sealed class WindowsBootSessionIdentityProviderTests
{
    [Fact]
    public void CurrentBootIdentity_IsStableAcrossReadsAndDoesNotUseWallClockTime()
    {
        var provider = new WindowsBootSessionIdentityProvider();

        var first = provider.GetCurrent();
        var second = provider.GetCurrent();

        Assert.True(first.IsAvailable, first.Error);
        Assert.Equal(first.Identity, second.Identity);
        Assert.Matches("^kuser-boot-v1:[0-9A-F]{8}$", first.Identity!);
    }
}
