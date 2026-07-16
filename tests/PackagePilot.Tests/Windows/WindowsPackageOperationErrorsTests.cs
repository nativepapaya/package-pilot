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
