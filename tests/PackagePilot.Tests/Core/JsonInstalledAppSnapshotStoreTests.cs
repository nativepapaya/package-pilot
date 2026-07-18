using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class JsonInstalledAppSnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "package-pilot-installed-cache-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_StripsAllActionableDescriptors()
    {
        var path = Path.Combine(_root, "snapshot.json");
        var store = new JsonInstalledAppSnapshotStore(path);
        var packageFullName = "Contoso_1.0.0.0_x64__publisher";
        await store.SaveAsync(new InstalledAppSnapshot
        {
            Apps =
            [
                new InstalledApp
                {
                    Id = "app-1",
                    Name = "Contoso",
                    Installations =
                    [
                        new Installation
                        {
                            Provider = InstalledAppProviderKind.Msix,
                            PackageFullName = packageFullName,
                            SupportsDirectRemoval = true,
                            WingetPackage = new PackageKey("Contoso.App", "source")
                        }
                    ],
                    Actions =
                    [
                        new InstalledAppActionDescriptor
                        {
                            Kind = InstalledAppActionKind.RemoveMsix,
                            PackageFullName = packageFullName,
                            IsPrimary = true
                        }
                    ]
                }
            ]
        });

        var loaded = await store.LoadAsync();

        var app = Assert.Single(loaded!.Apps);
        Assert.Empty(app.Actions);
        var installation = Assert.Single(app.Installations);
        Assert.Null(installation.PackageFullName);
        Assert.False(installation.SupportsDirectRemoval);
        Assert.Equal("Contoso.App", installation.WingetPackage?.Id);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task CorruptCache_IsIgnoredWithoutBlockingStartup()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "snapshot.json");
        await File.WriteAllTextAsync(path, "{not-json");

        var loaded = await new JsonInstalledAppSnapshotStore(path).LoadAsync();

        Assert.Null(loaded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
