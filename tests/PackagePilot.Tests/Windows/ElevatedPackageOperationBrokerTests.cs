using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Windows;

public sealed class ElevatedPackageOperationBrokerTests
{
    [Fact]
    public void Availability_RequiresBothPackagedHostEligibilityAndHelperFile()
    {
        var helper = Path.Combine(
            Path.GetTempPath(),
            $"PackagePilot.PackageAdmin.{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllText(helper, string.Empty);
            Assert.True(new ElevatedPackageOperationBroker(
                helper,
                connectionTimeout: null,
                operationTimeout: null,
                hostEligibility: _ => true).IsAvailable);
            Assert.False(new ElevatedPackageOperationBroker(
                helper,
                connectionTimeout: null,
                operationTimeout: null,
                hostEligibility: _ => false).IsAvailable);
        }
        finally
        {
            File.Delete(helper);
        }

        Assert.False(new ElevatedPackageOperationBroker(
            helper,
            connectionTimeout: null,
            operationTimeout: null,
            hostEligibility: _ => true).IsAvailable);
    }

    [Fact]
    public void RequestCreation_RequiresExplicitExactWinGetTargetAndApproval()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var valid = PackageOperation.Create(PackageOperationKind.Upgrade, package) with
        {
            RunAsAdministrator = true
        };

        Assert.True(ElevatedPackageOperationBroker.TryCreateRequest(valid, out _, out _));
        Assert.False(ElevatedPackageOperationBroker.TryCreateRequest(
            valid with { RunAsAdministrator = false },
            out _,
            out _));
        Assert.False(ElevatedPackageOperationBroker.TryCreateRequest(
            valid with { Target = null },
            out _,
            out _));
        Assert.False(ElevatedPackageOperationBroker.TryCreateRequest(
            valid with
            {
                Target = new WingetTarget
                {
                    Package = new PackageKey("Contoso.App", "other-source")
                }
            },
            out _,
            out _));
    }

    [Fact]
    public void ResponseValidation_RequiresExactPackageTargetAndAdministratorMarker()
    {
        var request = new PrivilegedPackageRequest
        {
            RequestId = Guid.NewGuid(),
            Kind = PackageOperationKind.Install,
            PackageId = "Contoso.App",
            SourceId = "winget"
        };
        var valid = Response(request);

        Assert.True(ElevatedPackageOperationBroker.IsValidResponse(valid, request));
        Assert.False(ElevatedPackageOperationBroker.IsValidResponse(
            valid with
            {
                Result = valid.Result with
                {
                    Package = new PackageKey("contoso.app", "winget")
                }
            },
            request));
        Assert.False(ElevatedPackageOperationBroker.IsValidResponse(
            valid with
            {
                Result = valid.Result with
                {
                    Target = new WingetTarget
                    {
                        Package = new PackageKey("Contoso.App", "Winget")
                    }
                }
            },
            request));
        Assert.False(ElevatedPackageOperationBroker.IsValidResponse(
            valid with { Result = valid.Result with { RanAsAdministrator = false } },
            request));
        Assert.False(ElevatedPackageOperationBroker.IsValidResponse(
            valid with
            {
                Result = valid.Result with { AdministratorRetryRequested = false }
            },
            request));
    }

    [Fact]
    public void ElevatedPipeAcl_IsProtectedAndAllowsOnlyTheInitiatingUser()
    {
        var pipeName = PackageAdminPipeProtocol.CreatePipeName();
        using var server = ElevatedPipeAclFactory.CreateServerForCurrentUser(pipeName);
        using var identity = WindowsIdentity.GetCurrent();
        var expectedUser = Assert.IsType<SecurityIdentifier>(identity.User);

        var security = server.GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(expectedUser, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();
        var rule = Assert.Single(rules);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(expectedUser, rule.IdentityReference);
        Assert.Equal(
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            rule.PipeAccessRights);
        Assert.Equal(
            (PipeAccessRights)0,
            rule.PipeAccessRights & PipeAccessRights.CreateNewInstance);
        Assert.Equal(
            (PipeAccessRights)0,
            rule.PipeAccessRights & PipeAccessRights.ChangePermissions);
        Assert.Equal(
            (PipeAccessRights)0,
            rule.PipeAccessRights & PipeAccessRights.TakeOwnership);
    }

    [Fact]
    public void ElevatedPipeAcl_RejectsAllNonAllowlistedPipeNames()
    {
        Assert.Throws<ArgumentException>(() =>
            ElevatedPipeAclFactory.CreateServerForCurrentUser("PackagePilot.Other.abc"));
        Assert.Throws<ArgumentException>(() =>
            ElevatedPipeAclFactory.CreateClient("PackagePilot.PackageAdmin.not-a-guid"));
    }

    [Fact]
    public void ServerIdentity_RequiresExactProtectedImageAndPackagedServer()
    {
        const string installed = @"C:\Program Files\WindowsApps\PackagePilot";
        const string expected = installed + @"\PackagePilot.App.exe";
        const string expectedFamily = "PackagePilot.Desktop_expectedpublisher";

        Assert.True(ElevatedPipeServerVerifier.IsTrustedServerIdentity(
            expected,
            expectedFamily,
            installed,
            installed,
            expectedFamily,
            helperPackageFamily: null));
        Assert.True(ElevatedPipeServerVerifier.IsTrustedServerIdentity(
            expected.ToUpperInvariant(),
            expectedFamily.ToUpperInvariant(),
            installed,
            installed,
            expectedFamily,
            expectedFamily));
        Assert.False(ElevatedPipeServerVerifier.IsTrustedServerIdentity(
            @"C:\Temp\PackagePilot.App.exe",
            expectedFamily,
            installed,
            installed,
            expectedFamily,
            expectedFamily));
        Assert.False(ElevatedPipeServerVerifier.IsTrustedServerIdentity(
            expected,
            serverPackageFamily: null,
            serverPackageInstallPath: installed,
            helperBaseDirectory: installed,
            expectedPackageFamily: expectedFamily,
            helperPackageFamily: null));
        Assert.False(ElevatedPipeServerVerifier.IsTrustedServerIdentity(
            expected,
            "Attacker_family",
            installed,
            installed,
            expectedFamily,
            helperPackageFamily: null));
        Assert.False(ElevatedPipeServerVerifier.IsTrustedServerIdentity(
            expected,
            expectedFamily,
            installed,
            installed,
            expectedFamily,
            "Attacker_family"));
        Assert.False(ElevatedPipeServerVerifier.IsTrustedServerIdentity(
            @"C:\Users\Buffy\AppData\Local\PackagePilot\PackagePilot.App.exe",
            expectedFamily,
            @"C:\Users\Buffy\AppData\Local\PackagePilot",
            @"C:\Users\Buffy\AppData\Local\PackagePilot",
            expectedFamily,
            helperPackageFamily: null));
        Assert.True(ElevatedPipeServerVerifier.IsProtectedPackageInstallLocation(
            @"D:\WindowsApps\PackagePilot.Desktop_1.0.0.0_x64__publisher"));
        Assert.False(ElevatedPipeServerVerifier.IsProtectedPackageInstallLocation(
            @"C:\Users\Buffy\WindowsApps\PackagePilot.Desktop_1.0.0.0_x64__publisher"));
        Assert.False(ElevatedPipeServerVerifier.IsProtectedPackageInstallLocation(
            @"\\server\share\WindowsApps\PackagePilot.Desktop_1.0.0.0_x64__publisher"));

        Assert.True(ElevatedPipeServerVerifier.IsEligiblePackageAdminHost(
            expected,
            expectedFamily,
            installed,
            installed,
            installed + @"\PackagePilot.PackageAdmin.exe",
            expectedFamily));
        Assert.False(ElevatedPipeServerVerifier.IsEligiblePackageAdminHost(
            expected,
            expectedFamily,
            installed,
            installed,
            installed + @"\Other.exe",
            expectedFamily));
        Assert.False(ElevatedPipeServerVerifier.IsEligiblePackageAdminHost(
            expected,
            expectedFamily,
            installed,
            installed,
            @"C:\Temp\PackagePilot.PackageAdmin.exe",
            expectedFamily));
    }

    private static PrivilegedPackageResponse Response(PrivilegedPackageRequest request)
    {
        var package = new PackageKey(request.PackageId, request.SourceId);
        return new PrivilegedPackageResponse
        {
            RequestId = request.RequestId,
            Result = new OperationResult
            {
                OperationId = request.RequestId,
                Package = package,
                Target = new WingetTarget { Package = package },
                Kind = request.Kind,
                State = PackageOperationState.Completed,
                RanAsAdministrator = true,
                AdministratorRetryRequested = true
            }
        };
    }
}
