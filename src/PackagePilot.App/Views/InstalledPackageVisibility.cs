namespace PackagePilot.App.Views;

internal static class InstalledPackageVisibility
{
    public static bool ShouldShow(
        PackageListItem package,
        bool showWindowsManagedApps) =>
        showWindowsManagedApps
        || !package.IsManageabilityKnown
        || package.IsManageableByPackagePilot;

    public static int CountWindowsManaged(IEnumerable<PackageListItem> packages) =>
        packages.Count(package =>
            package.IsManageabilityKnown
            && !package.IsManageableByPackagePilot);
}
