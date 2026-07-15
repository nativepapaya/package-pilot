using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class UpdateNotificationPolicyTests
{
    private readonly UpdateNotificationPolicy _policy = new();

    [Fact]
    public void NewFingerprint_UpdatesBadgeAndRequestsOneReplacement()
    {
        var decision = _policy.Evaluate(
            [Fingerprint("one", "1.0")],
            [Fingerprint("one", "1.0"), Fingerprint("two", "2.0")],
            isForegroundWindowActive: false);

        Assert.Equal(2, decision.BadgeCount);
        Assert.True(decision.ShowOrReplaceNotification);
        Assert.False(decision.ClearNotification);
        Assert.Single(decision.AddedUpdates);
        Assert.Equal("two", decision.AddedUpdates[0].PackageId);
    }

    [Fact]
    public void SameFingerprints_DoNotNotifyAgain()
    {
        var decision = _policy.Evaluate(
            [Fingerprint("one", "1.0")],
            [Fingerprint("ONE", "1.0")],
            isForegroundWindowActive: false);

        Assert.Equal(1, decision.BadgeCount);
        Assert.False(decision.ShowOrReplaceNotification);
    }

    [Fact]
    public void ForegroundScan_SuppressesNotificationButKeepsBadge()
    {
        var decision = _policy.Evaluate([], [Fingerprint("one", "1.0")], isForegroundWindowActive: true);

        Assert.Equal(1, decision.BadgeCount);
        Assert.False(decision.ShowOrReplaceNotification);
        Assert.Single(decision.AddedUpdates);
    }

    [Fact]
    public void ReachingZero_ClearsBadgeAndPriorNotification()
    {
        var decision = _policy.Evaluate([Fingerprint("one", "1.0")], [], isForegroundWindowActive: false);

        Assert.Equal(0, decision.BadgeCount);
        Assert.True(decision.ClearNotification);
        Assert.False(decision.ShowOrReplaceNotification);
    }

    [Fact]
    public void FailedCheck_PreservesPriorBadgeAndDoesNotNotify()
    {
        var decision = _policy.Evaluate(
            [Fingerprint("one", "1.0")],
            [],
            isForegroundWindowActive: false,
            checkSucceeded: false);

        Assert.Equal(1, decision.BadgeCount);
        Assert.False(decision.ClearNotification);
        Assert.False(decision.ShowOrReplaceNotification);
    }

    private static UpdateFingerprint Fingerprint(string id, string version) => new()
    {
        SourceId = "winget",
        PackageId = id,
        AvailableVersion = version
    };
}
