using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class JsonOperationHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "PackagePilot.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsResultsAndCapsHistoryAtOneHundred()
    {
        var path = Path.Combine(_directory, "history.json");
        var store = new JsonOperationHistoryStore(path);
        var source = Enumerable.Range(0, 105)
            .Select(index => Result(index, index == 0 ? PackageOperationState.Failed : PackageOperationState.Completed))
            .ToArray();

        await store.SaveAsync(source);
        var loaded = await store.LoadAsync();

        Assert.Equal(100, loaded.Count);
        Assert.Equal(source[0] with { Diagnostic = source[0].EffectiveDiagnostic }, loaded[0]);
        Assert.Equal(source[99] with { Diagnostic = source[99].EffectiveDiagnostic }, loaded[^1]);
        Assert.Equal(WingetErrorKind.Network, loaded[0].Error?.Kind);
    }

    [Fact]
    public async Task Load_ReturnsEmptyWhenFileDoesNotExist()
    {
        var store = new JsonOperationHistoryStore(Path.Combine(_directory, "missing.json"));

        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task Load_ReportsMalformedJsonAsInvalidData()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        await File.WriteAllTextAsync(path, "{ definitely not valid json }");
        var store = new JsonOperationHistoryStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task Load_MigratesLegacyPackageKeyToWingetTarget()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        await File.WriteAllTextAsync(path, """
            [
              {
                "operationId": "0190d474-2f40-7000-8000-000000000001",
                "package": { "id": "Contoso.Tool", "sourceId": "winget" },
                "kind": "Install",
                "state": "Completed",
                "startedAt": "2024-01-01T00:00:00+00:00",
                "completedAt": "2024-01-01T00:01:00+00:00"
              }
            ]
            """);

        var result = Assert.Single(await new JsonOperationHistoryStore(path).LoadAsync());

        var target = Assert.IsType<WingetTarget>(result.Target);
        Assert.Equal(new PackageKey("Contoso.Tool", "winget"), target.Package);
        Assert.Equal(OperationDiagnosticProvider.Winget, result.Diagnostic?.Provider);
        Assert.Equal(result.OperationId, result.Diagnostic?.ReferenceId);
        Assert.False(result.AdministratorRetryRequested);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAdministratorRetryIntent()
    {
        var path = Path.Combine(_directory, "history.json");
        var store = new JsonOperationHistoryStore(path);
        var source = Result(1, PackageOperationState.Failed) with
        {
            AdministratorRetryRequested = true,
            RanAsAdministrator = false,
            Error = new WingetError
            {
                Kind = WingetErrorKind.ElevationDenied,
                Code = "ElevationCancelled",
                Message = "Administrator approval was canceled."
            }
        };

        await store.SaveAsync([source]);
        var loaded = Assert.Single(await store.LoadAsync());

        Assert.True(loaded.AdministratorRetryRequested);
        Assert.False(loaded.RanAsAdministrator);
        Assert.Equal(WingetErrorKind.ElevationDenied, loaded.Error?.Kind);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsProviderNeutralDiagnosticReferences()
    {
        var path = Path.Combine(_directory, "history.json");
        var store = new JsonOperationHistoryStore(path);
        var winget = Result(1, PackageOperationState.Completed);
        var activityId = Guid.NewGuid();
        var msix = Result(2, PackageOperationState.Failed) with
        {
            Package = PackageKey.Empty,
            Target = new MsixTarget
            {
                PackageFullName = "Contoso.App_1.0.0.0_x64__publisher",
                PackageFamilyName = "Contoso.App_publisher"
            },
            Diagnostic = new OperationDiagnosticReference
            {
                Provider = OperationDiagnosticProvider.WindowsDeployment,
                ReferenceId = activityId
            }
        };

        await store.SaveAsync([winget, msix]);
        var loaded = await store.LoadAsync();

        Assert.Equal(OperationDiagnosticProvider.Winget, loaded[0].Diagnostic?.Provider);
        Assert.Equal(winget.OperationId, loaded[0].Diagnostic?.ReferenceId);
        Assert.Equal(OperationDiagnosticProvider.WindowsDeployment, loaded[1].Diagnostic?.Provider);
        Assert.Equal(activityId, loaded[1].Diagnostic?.ReferenceId);
    }

    [Fact]
    public async Task Load_DropsInvalidDiagnosticReferenceWithoutRejectingHistory()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        await File.WriteAllTextAsync(path, """
            [
              {
                "operationId": "0190d474-2f40-7000-8000-000000000001",
                "package": { "id": "", "sourceId": "" },
                "target": {
                  "$target": "msix",
                  "packageFullName": "Contoso.App_1.0.0.0_x64__publisher",
                  "packageFamilyName": "Contoso.App_publisher"
                },
                "kind": "Uninstall",
                "state": "Failed",
                "startedAt": "2024-01-01T00:00:00+00:00",
                "completedAt": "2024-01-01T00:01:00+00:00",
                "diagnostic": { "provider": 999, "referenceId": "00000000-0000-0000-0000-000000000000" }
              }
            ]
            """);

        var result = Assert.Single(await new JsonOperationHistoryStore(path).LoadAsync());

        Assert.Null(result.Diagnostic);
        Assert.Null(result.EffectiveDiagnostic);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static OperationResult Result(int index, PackageOperationState state) => new()
    {
        OperationId = Guid.CreateVersion7(),
        Package = new PackageKey($"Package.{index}", "winget"),
        Target = new WingetTarget { Package = new PackageKey($"Package.{index}", "winget") },
        Kind = PackageOperationKind.Install,
        State = state,
        StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(index),
        CompletedAt = DateTimeOffset.UnixEpoch.AddMinutes(index + 1),
        Error = state == PackageOperationState.Failed
            ? new WingetError
            {
                Kind = WingetErrorKind.Network,
                Code = "Network",
                Message = "No network."
            }
            : null
    };
}
