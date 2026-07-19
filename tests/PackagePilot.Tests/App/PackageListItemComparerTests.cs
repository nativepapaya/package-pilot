using PackagePilot.App.Views;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.App;

public sealed class PackageListItemComparerTests
{
    [Fact]
    public void HaveSameRows_UsesValueEqualityForEquivalentRows()
    {
        Assert.True(PackageListItemComparer.HaveSameRows([CreateItem()], [CreateItem()]));
    }

    [Fact]
    public void HaveSameRows_DetectsElevationChangesUsedByUpdateReview()
    {
        var current = CreateItem();
        var replacement = CreateItem();
        replacement.RequiresElevation = true;

        Assert.False(PackageListItemComparer.HaveSameRows([current], [replacement]));
    }

    [Fact]
    public void HaveSameRows_DetectsActionAvailabilityChanges()
    {
        var current = CreateItem();
        var replacement = CreateItem();
        replacement.IsActionEnabled = false;

        Assert.False(PackageListItemComparer.HaveSameRows([current], [replacement]));
    }

    [Fact]
    public void HaveSameRows_DetectsOperationStateChanges()
    {
        var current = CreateItem();
        var replacement = CreateItem();
        replacement.OperationState = PackageOperationState.Queued;

        Assert.False(PackageListItemComparer.HaveSameRows([current], [replacement]));
    }

    [Fact]
    public void HaveSameRows_DetectsAdministratorRetryChanges()
    {
        var current = CreateItem();
        var replacement = CreateItem();
        replacement.RequiresAdministratorRetry = true;

        Assert.False(PackageListItemComparer.HaveSameRows([current], [replacement]));
    }

    [Fact]
    public void SameRowsExceptFeedback_AllowsAnInPlaceTransition()
    {
        var current = CreateItem();
        var replacement = CreateItem();
        replacement.Status = "Queued - waiting to start";
        replacement.ActionLabel = "Queued";
        replacement.IsActionEnabled = false;
        replacement.OperationState = PackageOperationState.Queued;
        replacement.OperationErrorKind = WingetErrorKind.AdministratorRequired;
        replacement.VerificationPhase = MutationVerificationPhase.OutcomeUnknown;
        replacement.RequiresAdministratorRetry = true;

        Assert.True(PackageListItemComparer.HaveSameRowsExceptOperationFeedback(
            [current],
            [replacement]));
    }

    [Fact]
    public void ApplyOperationFeedback_NotifiesEveryDynamicBinding()
    {
        var current = CreateItem();
        var replacement = CreateItem();
        replacement.Status = "Queued - waiting to start";
        replacement.ActionLabel = "Queued";
        replacement.IsActionEnabled = false;
        replacement.OperationState = PackageOperationState.Queued;
        replacement.OperationErrorKind = WingetErrorKind.AdministratorRequired;
        replacement.VerificationPhase = MutationVerificationPhase.OutcomeUnknown;
        replacement.RequiresAdministratorRetry = true;
        var changed = new HashSet<string>();
        current.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        current.ApplyOperationFeedback(replacement);

        Assert.Contains(nameof(PackageListItem.Status), changed);
        Assert.Contains(nameof(PackageListItem.ActionLabel), changed);
        Assert.Contains(nameof(PackageListItem.IsActionEnabled), changed);
        Assert.Contains(nameof(PackageListItem.OperationState), changed);
        Assert.Contains(nameof(PackageListItem.OperationErrorKind), changed);
        Assert.Contains(nameof(PackageListItem.VerificationPhase), changed);
        Assert.Contains(nameof(PackageListItem.RequiresAdministratorRetry), changed);
    }

    [Fact]
    public void HaveSameRows_DetectsRequestedOperationKindChanges()
    {
        var current = CreateItem();
        var replacement = CreateItem();
        replacement.RequestedOperationKind = PackageOperationKind.Install;

        Assert.False(PackageListItemComparer.HaveSameRows([current], [replacement]));
    }

    [Fact]
    public void HaveSameRows_DetectsManageabilityChanges()
    {
        var current = CreateItem();
        var replacement = CreateItem();
        replacement.IsManageabilityKnown = true;
        replacement.IsManageableByPackagePilot = true;

        Assert.False(PackageListItemComparer.HaveSameRows([current], [replacement]));
    }

    private static PackageListItem CreateItem() => new()
    {
        Name = "Package",
        Publisher = "Publisher",
        PackageId = "Publisher.Package",
        Source = "winget",
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
        Status = "UpdateAvailable",
        ActionLabel = "Update",
        RequestedOperationKind = PackageOperationKind.Upgrade,
        WingetPackage = new PackageKey("Publisher.Package", "winget"),
        IconUri = new Uri("https://example.test/icon.png")
    };
}
