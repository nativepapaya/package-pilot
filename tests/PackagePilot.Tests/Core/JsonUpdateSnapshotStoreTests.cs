using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class JsonUpdateSnapshotStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "PackagePilot.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsVersionedSnapshot()
    {
        var path = Path.Combine(_directory, "updates.json");
        var store = new JsonUpdateSnapshotStore(path);
        var snapshot = Snapshot("2.0");

        await store.SaveAsync(snapshot);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(UpdateSnapshot.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(snapshot.LastAttemptAt, loaded.LastAttemptAt);
        Assert.Equal(snapshot.LastSuccessAt, loaded.LastSuccessAt);
        Assert.Equal(snapshot.Updates[0], loaded.Updates[0]);
        Assert.Equal(snapshot.Fingerprints[0], loaded.Fingerprints[0]);
    }

    [Fact]
    public async Task Load_IgnoresMalformedAndUnsupportedCacheFiles()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "updates.json");
        var store = new JsonUpdateSnapshotStore(path);

        await File.WriteAllTextAsync(path, "{ definitely not json }");
        Assert.Null(await store.LoadAsync());

        await File.WriteAllTextAsync(path, "{ \"schemaVersion\": 999 }");
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task CancelledSave_PreservesPreviousSnapshotAndRemovesTemporaryFile()
    {
        var path = Path.Combine(_directory, "updates.json");
        var store = new JsonUpdateSnapshotStore(path);
        await store.SaveAsync(Snapshot("1.0"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(Snapshot("2.0"), cancellation.Token));

        var loaded = await store.LoadAsync();
        Assert.Equal("1.0", loaded?.Updates[0].AvailableVersion);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static UpdateSnapshot Snapshot(string version)
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        return new UpdateSnapshot
        {
            LastAttemptAt = timestamp,
            LastSuccessAt = timestamp,
            Updates =
            [
                new PackageSummary
                {
                    Key = new PackageKey("Contoso.Tool", "winget"),
                    Name = "Contoso Tool",
                    InstalledVersion = "0.9",
                    AvailableVersion = version,
                    Status = PackageStatus.UpdateAvailable
                }
            ],
            Sources =
            [
                new UpdateSourceHealthSnapshot
                {
                    Id = "winget",
                    Name = "winget",
                    Health = SourceHealth.Healthy
                }
            ],
            Fingerprints =
            [
                new UpdateFingerprint
                {
                    SourceId = "winget",
                    PackageId = "contoso.tool",
                    AvailableVersion = version
                }
            ]
        };
    }
}
