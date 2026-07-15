using PackagePilot.App.Views;

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
        IconUri = new Uri("https://example.test/icon.png")
    };
}
