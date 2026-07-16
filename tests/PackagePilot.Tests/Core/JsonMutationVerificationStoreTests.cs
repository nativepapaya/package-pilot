using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class JsonMutationVerificationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "PackagePilot.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_RoundTripsMaximumUpdateListBeyondLocalSettingsLimit()
    {
        var path = Path.Combine(_directory, "mutation-verifications.json");
        var store = new JsonMutationVerificationStore(path);
        var markers = Enumerable.Range(0, 100)
            .Select(index => Marker(index, new string((char)('A' + (index % 26)), 96)))
            .ToArray();
        var verified = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();

        store.Save(Snapshot(markers, verified));
        var loaded = store.Load();

        Assert.True(new FileInfo(path).Length > 8 * 1024);
        Assert.NotNull(loaded);
        Assert.Equal(MutationVerificationSnapshot.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(100, loaded.Markers.Count);
        Assert.Equal(markers[99], loaded.Markers[99]);
        Assert.Equal(verified, loaded.VerifiedOperationIds);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("{ definitely not json }")]
    [InlineData("{ \"schemaVersion\": 999, \"markers\": [], \"verifiedOperationIds\": [] }")]
    [InlineData("{ \"schemaVersion\": 2, \"verifiedOperationIds\": [] }")]
    [InlineData("{ \"schemaVersion\": 2, \"markers\": [] }")]
    [InlineData("{ \"schemaVersion\": 2, \"markers\": null, \"verifiedOperationIds\": [] }")]
    [InlineData("{ \"schemaVersion\": 2, \"markers\": [null], \"verifiedOperationIds\": [] }")]
    public void Load_RejectsMalformedUnsupportedAndIncompleteRecoveryFiles(string contents)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "mutation-verifications.json");
        var store = new JsonMutationVerificationStore(path);

        File.WriteAllText(path, contents);

        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void Load_RejectsEverySemanticallyInvalidMarkerAndDuplicateTarget()
    {
        var path = Path.Combine(_directory, "mutation-verifications.json");
        var store = new JsonMutationVerificationStore(path);
        var valid = Marker(1, "Valid");
        var invalidMarkers = new[]
        {
            valid with { OperationId = Guid.Empty },
            valid with { RevisionId = Guid.Empty },
            valid with { RecordedAt = default },
            valid with { BootSessionId = string.Empty },
            valid with { Kind = (PackageOperationKind)999 },
            valid with { Phase = (MutationVerificationPhase)999 },
            valid with { Package = valid.Package with { Key = PackageKey.Empty } },
            valid with
            {
                Package = valid.Package with
                {
                    Key = new PackageKey(valid.Package.Key.Id, string.Empty)
                }
            }
        };

        foreach (var marker in invalidMarkers)
        {
            Assert.Throws<InvalidDataException>(() => store.Save(Snapshot([marker])));
        }

        Assert.Throws<InvalidDataException>(() => store.Save(Snapshot([valid, valid])));
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(_directory)
            && Directory.EnumerateFiles(_directory, "*.tmp").Any());
    }

    [Fact]
    public void InvalidSave_PreservesPriorSnapshotAndCreatesNoTemporaryFile()
    {
        var path = Path.Combine(_directory, "mutation-verifications.json");
        var store = new JsonMutationVerificationStore(path);
        store.Save(Snapshot([Marker(1, "Original")]));

        Assert.Throws<InvalidDataException>(() => store.Save(Snapshot([
            Marker(2, "Invalid") with { OperationId = Guid.Empty }
        ])));

        var loaded = store.Load();
        Assert.Equal("Original", Assert.Single(loaded!.Markers).Package.Name);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void FailedAtomicReplacement_PreservesPriorSnapshotAndRemovesTemporaryFile()
    {
        var path = Path.Combine(_directory, "mutation-verifications.json");
        var store = new JsonMutationVerificationStore(path);
        store.Save(Snapshot([Marker(1, "Original")]));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = Record.Exception(() => store.Save(Snapshot([
                Marker(2, "Replacement")
            ])));
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }

        var loaded = store.Load();
        Assert.Equal("Original", Assert.Single(loaded!.Markers).Package.Name);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Load_DistinguishesMissingFileFromUnreadableExistingFile()
    {
        var path = Path.Combine(_directory, "mutation-verifications.json");
        var store = new JsonMutationVerificationStore(path);
        Assert.Null(store.Load());
        store.Save(Snapshot([Marker(1, "Locked")]));

        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var exception = Record.Exception(() => store.Load());

        Assert.True(exception is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public void Load_RejectsOversizedFileBeforeDeserialization()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "mutation-verifications.json");
        File.WriteAllBytes(path, new byte[(1024 * 1024) + 1]);

        Assert.Throws<InvalidDataException>(() =>
            new JsonMutationVerificationStore(path).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static MutationVerificationSnapshot Snapshot(
        IReadOnlyList<MutationVerificationMarker> markers,
        IReadOnlyList<Guid>? verifiedOperationIds = null) => new()
        {
            Markers = markers,
            VerifiedOperationIds = verifiedOperationIds ?? []
        };

    private static MutationVerificationMarker Marker(int index, string name)
    {
        var recordedAt = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)
            .AddMinutes(index);
        return new MutationVerificationMarker
        {
            OperationId = Guid.NewGuid(),
            RevisionId = Guid.NewGuid(),
            Kind = PackageOperationKind.Upgrade,
            Package = new PackageSummary
            {
                Key = new PackageKey($"Contoso.Tool.{index:D3}", "winget"),
                Name = name,
                Publisher = "Contoso",
                SourceName = "winget",
                InstalledVersion = "1.0.0",
                AvailableVersion = "2.0.0",
                Status = PackageStatus.UpdateAvailable
            },
            RecordedAt = recordedAt,
            BootSessionId = "kuser-boot-v1:00000011",
            Phase = index % 2 == 0
                ? MutationVerificationPhase.RestartRequired
                : MutationVerificationPhase.VerificationPending
        };
    }
}
