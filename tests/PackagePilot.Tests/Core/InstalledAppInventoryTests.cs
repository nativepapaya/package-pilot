using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class InstalledAppInventoryTests
{
    private const string FamilyName = "Contoso.App_abcd1234";
    private const string ProductCode = "{D4EDBFD8-47C8-4E8A-858B-F44C422AC8C3}";

    [Fact]
    public void Merger_JoinsWingetAndMsixByExactPackageFamilyName()
    {
        var apps = new ExactInstalledAppMerger().Merge(
        [
            Winget("winget-app", "Contoso from WinGet",
                new InstalledAppAlias(InstalledAppAliasKind.PackageFamilyName, FamilyName)),
            Msix("msix-app", "Different MSIX display name", FamilyName.ToUpperInvariant())
        ]);

        var app = Assert.Single(apps);
        Assert.Equal("Contoso from WinGet", app.Name);
        Assert.Equal(2, app.Installations.Count);
        Assert.Equal(InstalledAppActionKind.UninstallWithWinget, app.PrimaryAction?.Kind);
        Assert.Single(app.Aliases, alias =>
            alias.Kind == InstalledAppAliasKind.PackageFamilyName);
    }

    [Fact]
    public void Merger_JoinsWingetAndRegistryByCanonicalProductCode()
    {
        var apps = new ExactInstalledAppMerger().Merge(
        [
            Winget("winget-app", "Contoso",
                new InstalledAppAlias(
                    InstalledAppAliasKind.ProductCode,
                    "d4edbfd8-47c8-4e8a-858b-f44c422ac8c3")),
            Registry("registry-app", InstallerScope.Machine, ProductCode)
        ]);

        var app = Assert.Single(apps);
        Assert.Equal(2, app.Installations.Count);
        Assert.Contains(app.Aliases, alias =>
            alias.Kind == InstalledAppAliasKind.ProductCode && alias.Value == ProductCode);
    }

    [Fact]
    public void Merger_JoinsNonGuidProductCodesCaseInsensitively()
    {
        var apps = new ExactInstalledAppMerger().Merge(
        [
            Winget("winget-app", "Contoso",
                new InstalledAppAlias(InstalledAppAliasKind.ProductCode, "Contoso Product")),
            Registry("registry-app", InstallerScope.Machine, "contoso product")
        ]);

        Assert.Equal(2, Assert.Single(apps).Installations.Count);
    }

    [Fact]
    public void Merger_NeverUsesDisplayNamePublisherOrVersionAsJoinKeys()
    {
        var winget = Winget("winget-app", "Same name");
        var registry = Registry("registry-app", InstallerScope.Machine, "Different.Product.Code") with
        {
            DisplayName = "Same name",
            Publisher = winget.Publisher,
            Version = winget.Version
        };

        var apps = new ExactInstalledAppMerger().Merge([winget, registry]);

        Assert.Equal(2, apps.Count);
        Assert.All(apps, app => Assert.Single(app.Installations));
    }

    [Fact]
    public void Merger_PreservesMultipleUserAndMachineInstallations()
    {
        var apps = new ExactInstalledAppMerger().Merge(
        [
            Registry("registry-user", InstallerScope.User, ProductCode) with { Version = "1.0" },
            Registry("registry-machine", InstallerScope.Machine, ProductCode) with { Version = "2.0" }
        ]);

        var app = Assert.Single(apps);
        Assert.Equal(2, app.Installations.Count);
        Assert.Contains(app.Installations, installation => installation.Scope == InstallerScope.User);
        Assert.Contains(app.Installations, installation => installation.Scope == InstallerScope.Machine);
        Assert.True(app.HasMultipleVersions);
        Assert.Equal("Multiple versions", app.VersionDisplay);
    }

    [Fact]
    public async Task Inventory_PreservesHealthyProviderDataWhenAnotherProviderFails()
    {
        var inventory = new InstalledAppInventory(
        [
            new StubProvider(
                "healthy",
                InstalledAppProviderKind.Winget,
                new InstalledAppProviderResult { Installations = [Winget("winget-app", "Healthy")] }),
            new StubProvider(
                "failed",
                InstalledAppProviderKind.Registry,
                new InvalidOperationException("Registry access was denied."))
        ]);

        var snapshot = await inventory.GetSnapshotAsync();

        Assert.Single(snapshot.Apps);
        Assert.True(snapshot.IsPartial);
        Assert.Equal(InventoryProviderHealth.Healthy,
            snapshot.Providers.Single(status => status.ProviderId == "healthy").Health);
        var failed = snapshot.Providers.Single(status => status.ProviderId == "failed");
        Assert.Equal(InventoryProviderHealth.Unavailable, failed.Health);
        Assert.Contains("denied", failed.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merger_PrefersWingetUninstallOverDirectOrHandoffActions()
    {
        var apps = new ExactInstalledAppMerger().Merge(
        [
            Winget(
                "winget-app",
                "Contoso",
                new InstalledAppAlias(InstalledAppAliasKind.PackageFamilyName, FamilyName),
                new InstalledAppAlias(InstalledAppAliasKind.ProductCode, ProductCode)),
            Msix("msix-app", "Contoso", FamilyName) with { IsStoreApp = true },
            Registry("registry-app", InstallerScope.Machine, ProductCode)
        ]);

        var app = Assert.Single(apps);
        var action = Assert.Single(app.Actions);
        Assert.Equal(InstalledAppActionKind.UninstallWithWinget, action.Kind);
        Assert.True(action.IsPrimary);
    }

    [Fact]
    public void Merger_RoutesSyntheticWingetMsixToCurrentDirectRemovalTarget()
    {
        const string currentPackageFullName =
            "Claude_1.21459.3.0_x64__pzs8sxrjxfjjc";
        var syntheticWinget = Winget(
            "winget-msix",
            "Claude",
            new InstalledAppAlias(InstalledAppAliasKind.PackageFamilyName, FamilyName)) with
        {
            WingetPackage = new PackageKey(
                "MSIX\\Claude_1.20186.1.0_x64__pzs8sxrjxfjjc",
                "*PredefinedInstalledSource")
        };
        var currentMsix = Msix("msix-current", "Claude", FamilyName) with
        {
            PackageFullName = currentPackageFullName
        };

        var app = Assert.Single(new ExactInstalledAppMerger().Merge(
            [syntheticWinget, currentMsix]));

        var action = Assert.Single(app.Actions);
        Assert.Equal(InstalledAppActionKind.RemoveMsix, action.Kind);
        Assert.Equal(currentPackageFullName, action.PackageFullName);
        Assert.True(action.IsPrimary);
        Assert.False(action.CanCancel);
    }

    [Fact]
    public void Merger_DoesNotRouteProtectedSyntheticMsixThroughWinget()
    {
        var syntheticWinget = Winget(
            "winget-msix",
            "Protected",
            new InstalledAppAlias(InstalledAppAliasKind.PackageFamilyName, FamilyName)) with
        {
            WingetPackage = new PackageKey(
                "MSIX\\Protected_1.0.0.0_x64__abcd1234",
                "*PredefinedInstalledSource")
        };
        var protectedMsix = Msix("msix-protected", "Protected", FamilyName) with
        {
            IsStoreApp = true,
            IsSystem = true
        };

        var app = Assert.Single(new ExactInstalledAppMerger().Merge(
            [syntheticWinget, protectedMsix]));

        Assert.DoesNotContain(app.Actions,
            action => action.Kind is InstalledAppActionKind.UninstallWithWinget
                or InstalledAppActionKind.RemoveMsix);
        Assert.Equal(InstalledAppActionKind.OpenStoreUpdates, app.PrimaryAction?.Kind);
    }

    [Fact]
    public void Merger_OffersNonCancelableMsixRemovalAndStoreHandoff()
    {
        var app = Assert.Single(new ExactInstalledAppMerger().Merge(
        [
            Msix("msix-app", "Contoso", FamilyName) with { IsStoreApp = true }
        ]));

        Assert.Equal(2, app.Actions.Count);
        var remove = app.Actions.Single(action => action.Kind == InstalledAppActionKind.RemoveMsix);
        Assert.True(remove.IsPrimary);
        Assert.True(remove.RequiresConfirmation);
        Assert.False(remove.CanCancel);
        Assert.False(app.Actions.Single(action =>
            action.Kind == InstalledAppActionKind.OpenStoreUpdates).IsPrimary);
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void Merger_BlocksDirectRemovalOfProtectedMsixPackages(
        bool system,
        bool framework,
        bool resource,
        bool optional,
        bool current)
    {
        var installation = Msix("msix-app", "Protected", FamilyName) with
        {
            IsStoreApp = true,
            IsSystem = system,
            IsFramework = framework,
            IsResourcePackage = resource,
            IsOptionalPackage = optional,
            IsCurrentApp = current
        };

        var app = Assert.Single(new ExactInstalledAppMerger().Merge([installation]));

        Assert.DoesNotContain(app.Actions,
            action => action.Kind == InstalledAppActionKind.RemoveMsix);
        Assert.Equal(InstalledAppActionKind.OpenStoreUpdates, app.PrimaryAction?.Kind);
    }

    [Fact]
    public void Merger_RoutesRegistryOnlyAppsToWindowsSettings()
    {
        var app = Assert.Single(new ExactInstalledAppMerger().Merge(
        [
            Registry("registry-app", InstallerScope.Machine, ProductCode)
        ]));

        var action = Assert.Single(app.Actions);
        Assert.Equal(InstalledAppActionKind.OpenInstalledApps, action.Kind);
        Assert.Equal("ms-settings", action.Destination?.Scheme);
    }

    [Fact]
    public async Task RegistryProvider_MapsProductCodeWithoutExposingAnUninstallAction()
    {
        var reader = new StubRegistryReader(new RegistryUninstallReadResult
        {
            Entries =
            [
                new RegistryUninstallEntry
                {
                    LocationId = "HKLM64",
                    SubKeyName = ProductCode,
                    DisplayName = "Contoso",
                    Scope = InstallerScope.Machine
                }
            ]
        });

        var result = await new RegistryInstalledAppProvider(reader).ReadAsync();

        var installation = Assert.Single(result.Installations);
        Assert.False(installation.SupportsDirectRemoval);
        Assert.Contains(installation.Aliases, alias =>
            alias.Kind == InstalledAppAliasKind.ProductCode && alias.Value == ProductCode);
    }

    private static Installation Winget(
        string id,
        string name,
        params InstalledAppAlias[] exactAliases) => new()
    {
        Id = id,
        ProviderId = WingetInstalledAppProvider.ProviderId,
        Provider = InstalledAppProviderKind.Winget,
        DisplayName = name,
        Publisher = "Contoso Ltd.",
        Version = "1.0",
        WingetPackage = new PackageKey("Contoso.App", "installed"),
        Aliases =
        [
            new InstalledAppAlias(InstalledAppAliasKind.WingetPackageId, "Contoso.App"),
            .. exactAliases
        ]
    };

    private static Installation Msix(
        string id,
        string name,
        string familyName) => new()
    {
        Id = id,
        ProviderId = MsixInstalledAppProvider.ProviderId,
        Provider = InstalledAppProviderKind.Msix,
        DisplayName = name,
        Version = "1.0",
        PackageFullName = $"{familyName}_1.0.0.0_x64__abcd1234",
        SupportsDirectRemoval = true,
        Aliases =
        [
            new InstalledAppAlias(InstalledAppAliasKind.PackageFamilyName, familyName)
        ]
    };

    private static Installation Registry(
        string id,
        InstallerScope scope,
        string productCode) => new()
    {
        Id = id,
        ProviderId = RegistryInstalledAppProvider.ProviderId,
        Provider = InstalledAppProviderKind.Registry,
        DisplayName = "Registry app",
        Publisher = "Contoso Ltd.",
        Version = "1.0",
        Scope = scope,
        Aliases =
        [
            new InstalledAppAlias(InstalledAppAliasKind.ProductCode, productCode)
        ]
    };

    private sealed class StubProvider : IInstalledAppProvider
    {
        private readonly InstalledAppProviderResult? _result;
        private readonly Exception? _error;

        public StubProvider(
            string id,
            InstalledAppProviderKind kind,
            InstalledAppProviderResult result)
        {
            Id = id;
            Kind = kind;
            _result = result;
        }

        public StubProvider(string id, InstalledAppProviderKind kind, Exception error)
        {
            Id = id;
            Kind = kind;
            _error = error;
        }

        public string Id { get; }
        public InstalledAppProviderKind Kind { get; }

        public Task<InstalledAppProviderResult> ReadAsync(
            CancellationToken cancellationToken = default) =>
            _error is null
                ? Task.FromResult(_result!)
                : Task.FromException<InstalledAppProviderResult>(_error);
    }

    private sealed class StubRegistryReader(RegistryUninstallReadResult result)
        : IRegistryUninstallReader
    {
        public Task<RegistryUninstallReadResult> ReadAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
