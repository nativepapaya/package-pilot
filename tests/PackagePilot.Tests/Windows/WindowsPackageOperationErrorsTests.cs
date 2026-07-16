using Microsoft.Management.Deployment;
using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Windows;

public sealed class WindowsPackageOperationErrorsTests
{
    [Fact]
    public void PackagedServiceHResult_IsClassifiedAsAdministratorRequired()
    {
        var hresult = unchecked((int)0x80073D28);

        var kind = WindowsPackageOperationErrors.ClassifyAdministratorRequirement(
            hresult,
            WingetErrorKind.ComFailure);

        Assert.Equal(WingetErrorKind.AdministratorRequired, kind);
        Assert.Equal(WingetErrorKind.AdministratorRequired, WingetClient.ClassifyHResult(hresult));
    }

    [Fact]
    public void OtherHResult_PreservesTheCallerFallback()
    {
        var hresult = unchecked((int)0x800704C7);

        var kind = WindowsPackageOperationErrors.ClassifyAdministratorRequirement(
            hresult,
            WingetErrorKind.ElevationDenied);

        Assert.Equal(WingetErrorKind.ElevationDenied, kind);
    }

    [Fact]
    public void PackageInUseHResult_IsClassifiedAndExplainsTheSafeRetry()
    {
        var hresult = unchecked((int)0x80073D02);

        Assert.True(WindowsPackageOperationErrors.IsApplicationInUse(hresult));
        Assert.Equal(WingetErrorKind.ApplicationInUse, WingetClient.ClassifyHResult(hresult));
        var message = WindowsPackageOperationErrors.GetApplicationInUseMessage(
            PackageOperationKind.Upgrade);
        Assert.Contains("still running", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Close", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OkStatus_WithNegativePackageInUseExtendedError_IsNotSuccess()
    {
        Assert.True(WingetClient.IsSuccessfulInstallResult(
            InstallResultStatus.Ok,
            extendedError: null));
        Assert.False(WingetClient.IsSuccessfulInstallResult(
            InstallResultStatus.Ok,
            unchecked((int)0x80073D02)));
    }

    [Theory]
    [InlineData(PackageOperationKind.Install, "install or update")]
    [InlineData(PackageOperationKind.Upgrade, "install or update")]
    [InlineData(PackageOperationKind.Uninstall, "remove")]
    public void AdministratorMessage_IsSpecificToTheMutation(
        PackageOperationKind kind,
        string expectedAction)
    {
        var message = WindowsPackageOperationErrors.GetAdministratorRequiredMessage(kind);

        Assert.Contains("packaged service", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("administrator privileges", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedAction, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not retry automatically", message, StringComparison.OrdinalIgnoreCase);
    }
}
