using PackagePilot.App.Views;
using PackagePilot.Core.Models;

namespace PackagePilot.Tests.App;

public sealed class UpdateNavigationBadgeProjectorTests
{
    [Fact]
    public void EmptyInventoryHidesBadge()
    {
        var state = UpdateNavigationBadgeProjector.Create(
            [],
            [],
            new OperationQueueSnapshot());

        Assert.Equal(0, state.Count);
        Assert.False(state.IsVisible);
        Assert.Equal("No updates available", state.AutomationName);
    }

    [Fact]
    public void DuplicateExactKeysProduceOneNumericBadgeEntry()
    {
        var package = Summary("Contoso.App", "winget");

        var state = UpdateNavigationBadgeProjector.Create(
            [package, Summary("contoso.app", "WINGET")],
            [],
            new OperationQueueSnapshot());

        Assert.Equal(1, state.Count);
        Assert.True(state.IsVisible);
        Assert.Equal("1 update available", state.AutomationName);
    }

    [Fact]
    public void SameIdentifierFromDifferentSourcesRemainsDistinct()
    {
        var state = UpdateNavigationBadgeProjector.Create(
            [Summary("Contoso.App", "winget"), Summary("Contoso.App", "private")],
            [],
            new OperationQueueSnapshot());

        Assert.Equal(2, state.Count);
        Assert.Equal("2 updates available", state.AutomationName);
    }

    [Fact]
    public void VerificationAndQueuedUpgradesDoNotRequestAttention()
    {
        var verifying = Summary("Contoso.Verifying", "winget");
        var queued = Summary("Contoso.Queued", "winget");
        var actionable = Summary("Contoso.Actionable", "winget");
        var queue = new OperationQueueSnapshot
        {
            Pending =
            [
                new OperationQueueEntry(
                    PackageOperation.Create(
                        PackageOperationKind.Upgrade,
                        queued.Key,
                        queued.Name),
                    new OperationProgress { State = PackageOperationState.Queued })
            ]
        };

        var state = UpdateNavigationBadgeProjector.Create(
            [verifying, queued, actionable],
            [verifying],
            queue);

        Assert.Equal(1, state.Count);
    }

    [Fact]
    public void FailedHistoryDoesNotSuppressRetryableUpdate()
    {
        var package = Summary("Contoso.Retry", "winget");
        var queue = new OperationQueueSnapshot
        {
            History =
            [
                new OperationResult
                {
                    Package = package.Key,
                    Kind = PackageOperationKind.Upgrade,
                    State = PackageOperationState.Failed
                }
            ]
        };

        var state = UpdateNavigationBadgeProjector.Create([package], [], queue);

        Assert.Equal(1, state.Count);
        Assert.True(state.IsVisible);
    }

    private static PackageSummary Summary(string id, string source) => new()
    {
        Key = new PackageKey(id, source),
        Name = id,
        Status = PackageStatus.UpdateAvailable
    };
}
