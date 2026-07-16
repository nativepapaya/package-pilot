using PackagePilot.Core.Models;

namespace PackagePilot.Windows.Services;

internal static class WindowsPackageOperationErrors
{
    internal const uint PackagedServiceRequiresAdministratorHResult = 0x80073D28;
    internal const uint PackageInUseHResult = 0x80073D02;

    internal static bool IsAdministratorRequired(int? hresult) =>
        hresult is not null
        && unchecked((uint)hresult.Value) == PackagedServiceRequiresAdministratorHResult;

    internal static bool IsApplicationInUse(int? hresult) =>
        hresult is not null
        && unchecked((uint)hresult.Value) == PackageInUseHResult;

    internal static WingetErrorKind ClassifyAdministratorRequirement(
        int? hresult,
        WingetErrorKind fallback) =>
        IsAdministratorRequired(hresult)
            ? WingetErrorKind.AdministratorRequired
            : fallback;

    internal static string GetAdministratorRequiredMessage(PackageOperationKind? kind) => kind switch
    {
        PackageOperationKind.Uninstall =>
            "This MSIX package includes a packaged service. Windows requires administrator privileges to remove it. Package Pilot did not retry automatically.",
        PackageOperationKind.Install or PackageOperationKind.Upgrade =>
            "This MSIX package includes a packaged service. Windows requires administrator privileges to install or update it. Package Pilot did not retry automatically.",
        _ =>
            "This MSIX package includes a packaged service. Windows requires administrator privileges for this operation. Package Pilot did not retry automatically."
    };

    internal static string GetApplicationInUseMessage(PackageOperationKind? kind) => kind switch
    {
        PackageOperationKind.Uninstall =>
            "Windows could not remove this app because it is still running. Close the app completely, then retry.",
        PackageOperationKind.Install or PackageOperationKind.Upgrade =>
            "Windows could not finish this update because the app is still running. Close the app completely, then retry.",
        _ =>
            "Windows could not finish the package operation because the app is still running. Close the app completely, then retry."
    };
}
