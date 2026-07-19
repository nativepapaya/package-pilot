using PackagePilot.App.Views;

namespace PackagePilot.Tests.App;

public sealed class PackageListItemPresentationTests
{
    [Theory]
    [InlineData("Installed", false)]
    [InlineData("installed", false)]
    [InlineData("Multiple versions", true)]
    [InlineData("Cached — confirming", true)]
    [InlineData("Update available", true)]
    public void InstalledListSuppressesOnlyTheRedundantInstalledState(
        string status,
        bool expected)
    {
        var item = new PackageListItem { Status = status };

        Assert.Equal(expected, item.ShowInstalledRowState);
    }

    [Fact]
    public void StatusChangeNotifiesInstalledRowState()
    {
        var item = new PackageListItem { Status = "Installed" };
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.Status = "Multiple versions";

        Assert.Contains(nameof(PackageListItem.Status), changed);
        Assert.Contains(nameof(PackageListItem.ShowInstalledRowState), changed);
    }
}
