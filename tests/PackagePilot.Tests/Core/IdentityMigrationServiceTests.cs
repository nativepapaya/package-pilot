using System.Text.Json;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class IdentityMigrationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "PackagePilot.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExportAndImport_RoundTripsOnceAndDeletesOnlyCompletedHandoff()
    {
        var migrationPath = MigrationPath();
        var developmentHistoryPath = Path.Combine(_directory, "development", "history.json");
        var productionHistoryPath = Path.Combine(_directory, "production", "history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(developmentHistoryPath)!);
        await File.WriteAllTextAsync(
            developmentHistoryPath,
            "[{\"operationId\":\"4b80a6df-2264-4ef5-a89c-23422ac2aabb\",\"success\":true}]");
        var developmentSettings = new InMemorySettingsStore(new Dictionary<string, object>
        {
            ["theme"] = "dark",
            ["reduceMotion"] = true,
            ["sourceAgreementConsent:winget"] = "sha256:fingerprint"
        });
        var development = Service(
            migrationPath,
            "PackagePilot.Desktop|CN=PackagePilot.Dev",
            developmentSettings,
            developmentHistoryPath);

        var exported = await development.ExportAsync();

        Assert.Equal(migrationPath, exported.FilePath);
        Assert.Equal(3, exported.SettingCount);
        Assert.Equal(1, exported.OperationHistoryCount);
        Assert.True(File.Exists(migrationPath));

        var productionSettings = new InMemorySettingsStore(new Dictionary<string, object>
        {
            ["productionOnly"] = "preserved"
        });
        var production = Service(
            migrationPath,
            "PackagePilot|CN=Trusted Publisher",
            productionSettings,
            productionHistoryPath);

        var imported = await production.ImportOnceAsync();

        Assert.Equal(IdentityMigrationImportOutcome.Imported, imported.Outcome);
        Assert.Equal(3, imported.SettingCount);
        Assert.Equal("dark", productionSettings.Values["theme"]);
        Assert.Equal(true, productionSettings.Values["reduceMotion"]);
        Assert.Equal("sha256:fingerprint", productionSettings.Values["sourceAgreementConsent:winget"]);
        Assert.Equal("preserved", productionSettings.Values["productionOnly"]);
        Assert.IsType<string>(
            productionSettings.Values[IdentityMigrationService.CompletionMarkerSettingKey]);
        Assert.False(File.Exists(migrationPath));
        using (var history = JsonDocument.Parse(await File.ReadAllTextAsync(productionHistoryPath)))
        {
            Assert.Equal(JsonValueKind.Array, history.RootElement.ValueKind);
            Assert.Single(history.RootElement.EnumerateArray());
        }

        var secondAttempt = await production.ImportOnceAsync();

        Assert.Equal(IdentityMigrationImportOutcome.NoMigrationAvailable, secondAttempt.Outcome);
        Assert.Equal(2, productionSettings.ApplyCount);
    }

    [Fact]
    public async Task Import_ReexportFromRetiredIdentityIsCleanedWithoutReapplyingData()
    {
        var migrationPath = MigrationPath();
        var developmentSettings = new InMemorySettingsStore(new Dictionary<string, object>
        {
            ["theme"] = "dark"
        });
        var development = Service(
            migrationPath,
            "development",
            developmentSettings,
            Path.Combine(_directory, "development", "history.json"));
        var productionSettings = new InMemorySettingsStore();
        var production = Service(
            migrationPath,
            "production",
            productionSettings,
            Path.Combine(_directory, "production", "history.json"));
        await development.ExportAsync();
        Assert.Equal(
            IdentityMigrationImportOutcome.Imported,
            (await production.ImportOnceAsync()).Outcome);
        var completedApplyCount = productionSettings.ApplyCount;
        productionSettings.Values["theme"] = "production-choice";
        developmentSettings.Values["theme"] = "changed-after-retirement";

        await development.ExportAsync();
        var repeated = await production.ImportOnceAsync();

        Assert.Equal(IdentityMigrationImportOutcome.AlreadyImported, repeated.Outcome);
        Assert.Equal("production-choice", productionSettings.Values["theme"]);
        Assert.Equal(completedApplyCount, productionSettings.ApplyCount);
        Assert.False(File.Exists(migrationPath));
    }

    [Fact]
    public async Task Export_AtomicallyPreservesPriorHandoffWhenReplacementCannotCommit()
    {
        var migrationPath = MigrationPath();
        var historyPath = Path.Combine(_directory, "development", "history.json");
        var settings = new InMemorySettingsStore(new Dictionary<string, object>
        {
            ["theme"] = "light"
        });
        var service = Service(migrationPath, "development", settings, historyPath);
        await service.ExportAsync();
        var original = await File.ReadAllBytesAsync(migrationPath);
        settings.Values["theme"] = "dark";

        await using (var heldOpen = new FileStream(
                         migrationPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var exception = await Record.ExceptionAsync(() => service.ExportAsync());
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(migrationPath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(migrationPath)!,
            $".{Path.GetFileName(migrationPath)}.*.tmp"));
    }

    [Fact]
    public async Task Import_RecoversFlushedTemporaryExportAfterInterruptedRename()
    {
        var migrationPath = MigrationPath();
        var development = Service(
            migrationPath,
            "development",
            new InMemorySettingsStore(new Dictionary<string, object> { ["theme"] = "system" }),
            Path.Combine(_directory, "development", "history.json"));
        await development.ExportAsync();
        var interruptedPath = Path.Combine(
            Path.GetDirectoryName(migrationPath)!,
            $".{Path.GetFileName(migrationPath)}.{Guid.NewGuid():N}.tmp");
        File.Move(migrationPath, interruptedPath);
        var productionSettings = new InMemorySettingsStore();
        var production = Service(
            migrationPath,
            "production",
            productionSettings,
            Path.Combine(_directory, "production", "history.json"));

        var result = await production.ImportOnceAsync();

        Assert.Equal(IdentityMigrationImportOutcome.Imported, result.Outcome);
        Assert.True(result.RecoveredInterruptedExport);
        Assert.Equal("system", productionSettings.Values["theme"]);
        Assert.False(File.Exists(interruptedPath));
        Assert.False(File.Exists(migrationPath));
    }

    [Fact]
    public async Task Import_StrictlyRejectsUnsupportedSchemaWithoutChangingTarget()
    {
        var migrationPath = MigrationPath();
        var development = Service(
            migrationPath,
            "development",
            new InMemorySettingsStore(new Dictionary<string, object> { ["theme"] = "dark" }),
            Path.Combine(_directory, "development", "history.json"));
        await development.ExportAsync();
        var json = await File.ReadAllTextAsync(migrationPath);
        await File.WriteAllTextAsync(
            migrationPath,
            json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999", StringComparison.Ordinal));
        var productionSettings = new InMemorySettingsStore();
        var productionHistory = Path.Combine(_directory, "production", "history.json");
        var production = Service(
            migrationPath,
            "production",
            productionSettings,
            productionHistory);

        await Assert.ThrowsAsync<InvalidDataException>(() => production.ImportOnceAsync());

        Assert.Empty(productionSettings.Values);
        Assert.Equal(0, productionSettings.ApplyCount);
        Assert.False(File.Exists(productionHistory));
        Assert.True(File.Exists(migrationPath));
    }

    [Fact]
    public async Task Import_RetainsHandoffWhenSettingsFailAndSucceedsOnRetry()
    {
        var migrationPath = MigrationPath();
        await Service(
                migrationPath,
                "development",
                new InMemorySettingsStore(new Dictionary<string, object> { ["theme"] = "dark" }),
                Path.Combine(_directory, "development", "history.json"))
            .ExportAsync();
        var productionSettings = new InMemorySettingsStore { FailApply = true };
        var production = Service(
            migrationPath,
            "production",
            productionSettings,
            Path.Combine(_directory, "production", "history.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => production.ImportOnceAsync());

        Assert.True(File.Exists(migrationPath));
        productionSettings.FailApply = false;

        var result = await production.ImportOnceAsync();

        Assert.Equal(IdentityMigrationImportOutcome.Imported, result.Outcome);
        Assert.Equal("dark", productionSettings.Values["theme"]);
        Assert.False(File.Exists(migrationPath));
    }

    [Fact]
    public async Task Import_RetainsHandoffUntilHistoryIsDurable()
    {
        var migrationPath = MigrationPath();
        var developmentHistory = Path.Combine(_directory, "development", "history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(developmentHistory)!);
        await File.WriteAllTextAsync(developmentHistory, "[{\"success\":true}]");
        await Service(
                migrationPath,
                "development",
                new InMemorySettingsStore(new Dictionary<string, object> { ["theme"] = "dark" }),
                developmentHistory)
            .ExportAsync();
        var productionHistory = Path.Combine(_directory, "production", "history.json");
        Directory.CreateDirectory(productionHistory);
        var productionSettings = new InMemorySettingsStore();
        var production = Service(
            migrationPath,
            "production",
            productionSettings,
            productionHistory);

        var exception = await Record.ExceptionAsync(() => production.ImportOnceAsync());
        Assert.True(exception is IOException or UnauthorizedAccessException);

        Assert.True(File.Exists(migrationPath));
        Assert.Equal("dark", productionSettings.Values["theme"]);
        Assert.DoesNotContain(
            IdentityMigrationService.CompletionMarkerSettingKey,
            productionSettings.Values.Keys);
        Directory.Delete(productionHistory);

        var result = await production.ImportOnceAsync();

        Assert.Equal(IdentityMigrationImportOutcome.Imported, result.Outcome);
        Assert.False(File.Exists(migrationPath));
        Assert.True(File.Exists(productionHistory));
        Assert.Contains(
            IdentityMigrationService.CompletionMarkerSettingKey,
            productionSettings.Values.Keys);
    }

    [Fact]
    public async Task Import_DoesNotConsumeHandoffFromSameIdentity()
    {
        var migrationPath = MigrationPath();
        var developmentSettings = new InMemorySettingsStore(new Dictionary<string, object>
        {
            ["theme"] = "dark"
        });
        await Service(
                migrationPath,
                "development",
                developmentSettings,
                Path.Combine(_directory, "development", "history.json"))
            .ExportAsync();

        var result = await Service(
                migrationPath,
                "development",
                developmentSettings,
                Path.Combine(_directory, "development", "history.json"))
            .ImportOnceAsync();

        Assert.Equal(IdentityMigrationImportOutcome.SourceIdentityMatchesCurrent, result.Outcome);
        Assert.True(File.Exists(migrationPath));
        Assert.Equal(0, developmentSettings.ApplyCount);
    }

    [Fact]
    public void NeutralPath_IsStableUnderNonPackageLocalAppData()
    {
        var localAppData = Path.Combine(_directory, "LocalAppData");

        var path = IdentityMigrationPaths.GetNeutralFilePath(localAppData);

        Assert.Equal(
            Path.Combine(
                Path.GetFullPath(localAppData),
                "Package Pilot",
                "Identity Migration",
                IdentityMigrationPaths.FileName),
            path);
        Assert.DoesNotContain("Packages", path, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string MigrationPath() =>
        IdentityMigrationPaths.GetNeutralFilePath(Path.Combine(_directory, "LocalAppData"));

    private static IdentityMigrationService Service(
        string migrationPath,
        string identity,
        IIdentityMigrationSettingsStore settings,
        string historyPath) =>
        new(migrationPath, identity, settings, historyPath);

    private sealed class InMemorySettingsStore : IIdentityMigrationSettingsStore
    {
        public InMemorySettingsStore(IReadOnlyDictionary<string, object>? initial = null)
        {
            if (initial is not null)
            {
                foreach (var item in initial)
                {
                    Values[item.Key] = item.Value;
                }
            }
        }

        public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
        public bool FailApply { get; set; }
        public int ApplyCount { get; private set; }

        public ValueTask<IReadOnlyDictionary<string, object>> ReadAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, object> snapshot = new Dictionary<string, object>(
                Values,
                StringComparer.Ordinal);
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask ApplyAsync(
            IReadOnlyDictionary<string, object> settings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailApply)
            {
                throw new InvalidOperationException("Simulated settings import failure.");
            }

            foreach (var setting in settings)
            {
                Values[setting.Key] = setting.Value;
            }

            ApplyCount++;
            return ValueTask.CompletedTask;
        }
    }
}
