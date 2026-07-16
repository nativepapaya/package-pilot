using PackagePilot.App.Views;
using PackagePilot.Core.Models;

namespace PackagePilot.Tests.App;

public sealed class OperationRowProjectorTests
{
    [Fact]
    public void WingetResult_ExposesAccessibleLogAction()
    {
        var result = WingetResult("winget");

        var row = OperationRowProjector.FromResult(result);

        Assert.True(row.CanViewDiagnostic);
        Assert.Equal("WinGet", row.DiagnosticProviderLabel);
        Assert.Equal("View WinGet diagnostics for Contoso.App", row.DiagnosticAutomationName);
        Assert.Equal("View WinGet diagnostics", row.DiagnosticToolTip);
    }

    [Theory]
    [InlineData("msstore")]
    [InlineData("StoreEdgeFD")]
    public void MicrosoftStoreWingetResult_UsesHonestHandoffProviderLabel(string sourceId)
    {
        var row = OperationRowProjector.FromResult(WingetResult(sourceId));

        Assert.True(row.CanViewDiagnostic);
        Assert.Equal("WinGet / Microsoft Store", row.DiagnosticProviderLabel);
        Assert.Contains("WinGet / Microsoft Store", row.DiagnosticAutomationName);
    }

    [Fact]
    public void MsixResult_RequiresAnExactDeploymentActivityReference()
    {
        var target = new MsixTarget
        {
            PackageFullName = "Contoso.App_1.0.0.0_x64__publisher",
            PackageFamilyName = "Contoso.App_publisher"
        };
        var withoutReference = new OperationResult
        {
            OperationId = Guid.NewGuid(),
            Target = target,
            Kind = PackageOperationKind.Uninstall,
            State = PackageOperationState.Failed
        };
        var withReference = withoutReference with
        {
            Diagnostic = new OperationDiagnosticReference
            {
                Provider = OperationDiagnosticProvider.WindowsDeployment,
                ReferenceId = Guid.NewGuid()
            }
        };

        Assert.False(OperationRowProjector.FromResult(withoutReference).CanViewDiagnostic);
        Assert.True(OperationRowProjector.FromResult(withReference).CanViewDiagnostic);
        Assert.Equal(
            "Windows deployment",
            OperationRowProjector.FromResult(withReference).DiagnosticProviderLabel);
    }

    private static OperationResult WingetResult(string sourceId)
    {
        var id = Guid.NewGuid();
        var package = new PackageKey("Contoso.App", sourceId);
        return new OperationResult
        {
            OperationId = id,
            Package = package,
            Target = new WingetTarget { Package = package },
            Kind = PackageOperationKind.Upgrade,
            State = PackageOperationState.Completed,
            Diagnostic = new OperationDiagnosticReference
            {
                Provider = OperationDiagnosticProvider.Winget,
                ReferenceId = id
            }
        };
    }
}
