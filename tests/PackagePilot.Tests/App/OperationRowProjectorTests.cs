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
        Assert.True(row.IsHistory);
        Assert.False(row.ShowProgress);
        Assert.False(row.ShowCancel);
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

    [Fact]
    public void QueuedWingetEntry_ExposesAClearLiveLogAction()
    {
        var id = Guid.NewGuid();
        var package = new PackageKey("Contoso.App", "winget");
        var row = OperationRowProjector.FromEntry(new OperationQueueEntry(
            new PackageOperation
            {
                Id = id,
                Package = package,
                Target = new WingetTarget { Package = package },
                DisplayName = "Contoso",
                Kind = PackageOperationKind.Install
            },
            new OperationProgress
            {
                OperationId = id,
                State = PackageOperationState.Queued
            }));

        Assert.True(row.CanViewDiagnostic);
        Assert.True(row.IsLiveDiagnostic);
        Assert.False(row.IsHistory);
        Assert.Contains("Live log", row.Detail);
        Assert.False(row.ShowProgress);
        Assert.True(row.ShowCancel);
        Assert.Equal("View live WinGet diagnostics for Contoso.App", row.DiagnosticAutomationName);
        Assert.Equal("Live WinGet diagnostics", row.DiagnosticToolTip);
    }

    [Fact]
    public void RunningMsixEntry_ReportsLiveProgressWithoutInventingAnActivityId()
    {
        var id = Guid.NewGuid();
        var row = OperationRowProjector.FromEntry(new OperationQueueEntry(
            new PackageOperation
            {
                Id = id,
                Target = new MsixTarget
                {
                    PackageFullName = "Contoso.App_1.0.0.0_x64__publisher",
                    PackageFamilyName = "Contoso.App_publisher"
                },
                DisplayName = "Contoso",
                Kind = PackageOperationKind.Uninstall
            },
            new OperationProgress
            {
                OperationId = id,
                State = PackageOperationState.Uninstalling,
                Percent = 30,
                CancellationSupported = false
            }));

        Assert.True(row.CanViewDiagnostic);
        Assert.True(row.IsLiveDiagnostic);
        Assert.Equal("Windows deployment", row.DiagnosticProviderLabel);
        Assert.Equal("Live progress: 30%", row.Detail);
        Assert.True(row.ShowProgress);
        Assert.False(row.ShowCancel);
    }

    [Fact]
    public void PresentationComparison_DetectsProgressWithoutChurningIdenticalRows()
    {
        var id = Guid.NewGuid();
        var first = new OperationListItem
        {
            OperationId = id,
            PackageName = "Contoso",
            Status = "Downloading",
            Detail = "Downloading - 25%",
            Progress = 25,
            IsActive = true,
            ShowProgress = true,
            CanViewDiagnostic = true,
            IsLiveDiagnostic = true
        };
        var identical = new OperationListItem
        {
            OperationId = id,
            PackageName = "Contoso",
            Status = "Downloading",
            Detail = "Downloading - 25%",
            Progress = 25,
            IsActive = true,
            ShowProgress = true,
            CanViewDiagnostic = true,
            IsLiveDiagnostic = true
        };
        var advanced = new OperationListItem
        {
            OperationId = id,
            PackageName = "Contoso",
            Status = "Downloading",
            Detail = "Downloading - 50%",
            Progress = 50,
            IsActive = true,
            ShowProgress = true,
            CanViewDiagnostic = true,
            IsLiveDiagnostic = true
        };

        Assert.True(OperationRowProjector.HaveSamePresentation(first, identical));
        Assert.False(OperationRowProjector.HaveSamePresentation(first, advanced));
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
