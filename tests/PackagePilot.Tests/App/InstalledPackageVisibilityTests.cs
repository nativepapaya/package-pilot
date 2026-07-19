using PackagePilot.App.Views;

namespace PackagePilot.Tests.App;

public sealed class InstalledPackageVisibilityTests
{
    [Fact]
    public void DefaultFilter_ShowsDirectlyManageableApps()
    {
        var package = Package(known: true, manageable: true);

        Assert.True(InstalledPackageVisibility.ShouldShow(
            package,
            showWindowsManagedApps: false));
    }

    [Fact]
    public void DefaultFilter_HidesConfirmedWindowsManagedApps()
    {
        var package = Package(known: true, manageable: false);

        Assert.False(InstalledPackageVisibility.ShouldShow(
            package,
            showWindowsManagedApps: false));
    }

    [Fact]
    public void DefaultFilter_PreservesCachedRowsUntilManageabilityIsKnown()
    {
        var package = Package(known: false, manageable: false);

        Assert.True(InstalledPackageVisibility.ShouldShow(
            package,
            showWindowsManagedApps: false));
    }

    [Fact]
    public void ShowWindowsManaged_RevealsConfirmedWindowsManagedApps()
    {
        var package = Package(known: true, manageable: false);

        Assert.True(InstalledPackageVisibility.ShouldShow(
            package,
            showWindowsManagedApps: true));
    }

    [Fact]
    public void CountWindowsManaged_IgnoresManageableAndUnknownRows()
    {
        var packages = new[]
        {
            Package(known: true, manageable: false),
            Package(known: true, manageable: true),
            Package(known: false, manageable: false)
        };

        Assert.Equal(1, InstalledPackageVisibility.CountWindowsManaged(packages));
    }

    private static PackageListItem Package(bool known, bool manageable) => new()
    {
        PackageId = Guid.NewGuid().ToString("N"),
        IsManageabilityKnown = known,
        IsManageableByPackagePilot = manageable
    };
}
