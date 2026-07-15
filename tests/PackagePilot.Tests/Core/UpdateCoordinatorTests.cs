using System.Collections.Concurrent;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class UpdateCoordinatorTests
{
    [Fact]
    public async Task ManualCheck_PersistsNormalizedVersionedSnapshot()
    {
        var time = new ManualTimeProvider();
        var client = new FakeUpdateDiscoveryClient
        {
            GetUpdates = _ => Task.FromResult<IReadOnlyList<PackageSummary>>(
            [
                Update("Contoso.Tool", "WINGET", "2.0", "Contoso Tool"),
                Update("contoso.tool", "winget", "2.0", "Duplicate")
            ])
        };
        var store = new InMemoryUpdateSnapshotStore();
        var coordinator = new UpdateCoordinator(client, store, time);
        var sources = new[]
        {
            new PackageSourceStatus { Id = "winget", Name = "winget", Health = SourceHealth.Healthy }
        };

        var result = await coordinator.CheckAsync(UpdateCheckReason.Manual, sources);

        Assert.True(result.PerformedCheck);
        Assert.Equal(UpdateCheckState.Current, result.State);
        Assert.Equal(UpdateSnapshot.CurrentSchemaVersion, result.Snapshot.SchemaVersion);
        Assert.Equal(time.GetUtcNow(), result.Snapshot.LastAttemptAt);
        Assert.Equal(time.GetUtcNow(), result.Snapshot.LastSuccessAt);
        Assert.Null(result.Snapshot.LastError);
        Assert.Single(result.Snapshot.Updates);
        var fingerprint = Assert.Single(result.Snapshot.Fingerprints);
        Assert.Equal("winget", fingerprint.SourceId);
        Assert.Equal("contoso.tool", fingerprint.PackageId);
        Assert.Equal("2.0", fingerprint.AvailableVersion);
        Assert.Equal(SourceHealth.Healthy, Assert.Single(result.Snapshot.Sources).Health);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task FailedCheck_PreservesRowsAndSuppressesAutomaticRetryForOneHour()
    {
        var time = new ManualTimeProvider();
        var cachedUpdate = Update("Contoso.Tool", "winget", "1.5", "Contoso Tool");
        var previous = new UpdateSnapshot
        {
            LastAttemptAt = time.GetUtcNow().AddHours(-25),
            LastSuccessAt = time.GetUtcNow().AddHours(-25),
            Updates = [cachedUpdate],
            Fingerprints = [new UpdateFingerprint
            {
                SourceId = "winget",
                PackageId = "contoso.tool",
                AvailableVersion = "1.5"
            }]
        };
        var store = new InMemoryUpdateSnapshotStore(previous);
        var client = new FakeUpdateDiscoveryClient
        {
            GetUpdates = _ => Task.FromException<IReadOnlyList<PackageSummary>>(
                new IOException("Source is offline."))
        };
        var coordinator = new UpdateCoordinator(client, store, time);

        var failed = await coordinator.CheckAsync(UpdateCheckReason.Manual);
        var deferred = await coordinator.CheckAsync(UpdateCheckReason.Automatic);

        Assert.Equal(UpdateCheckState.Failed, failed.State);
        Assert.Equal("Source is offline.", failed.Snapshot.LastError);
        Assert.Equal(cachedUpdate, Assert.Single(failed.Snapshot.Updates));
        Assert.False(deferred.PerformedCheck);
        Assert.Equal(1, client.UpdateCallCount);

        time.Advance(TimeSpan.FromHours(1));
        client.GetUpdates = _ => Task.FromResult<IReadOnlyList<PackageSummary>>(Array.Empty<PackageSummary>());
        var retried = await coordinator.CheckAsync(UpdateCheckReason.Automatic);

        Assert.True(retried.PerformedCheck);
        Assert.Equal(UpdateCheckState.Current, retried.State);
        Assert.Empty(retried.Snapshot.Updates);
        Assert.Equal(2, client.UpdateCallCount);
    }

    [Fact]
    public async Task FailureRetryDelayStartsWhenDiscoveryFinishes()
    {
        var time = new ManualTimeProvider();
        var client = new FakeUpdateDiscoveryClient
        {
            GetUpdates = _ =>
            {
                time.Advance(TimeSpan.FromHours(2));
                return Task.FromException<IReadOnlyList<PackageSummary>>(
                    new IOException("Source timed out."));
            }
        };
        var coordinator = new UpdateCoordinator(
            client,
            new InMemoryUpdateSnapshotStore(),
            time);

        var failed = await coordinator.CheckAsync(UpdateCheckReason.Manual);

        Assert.Equal(time.GetUtcNow(), failed.Snapshot.LastAttemptAt);
        Assert.False(coordinator.ShouldAutomaticallyCheck(failed.Snapshot));
        time.Advance(TimeSpan.FromHours(1) - TimeSpan.FromMilliseconds(1));
        Assert.False(coordinator.ShouldAutomaticallyCheck(failed.Snapshot));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(coordinator.ShouldAutomaticallyCheck(failed.Snapshot));
    }

    [Fact]
    public async Task SuccessfulFreshnessStartsWhenDiscoveryFinishes()
    {
        var time = new ManualTimeProvider();
        var client = new FakeUpdateDiscoveryClient
        {
            GetUpdates = _ =>
            {
                time.Advance(TimeSpan.FromMinutes(45));
                return Task.FromResult<IReadOnlyList<PackageSummary>>(
                    Array.Empty<PackageSummary>());
            }
        };
        var coordinator = new UpdateCoordinator(
            client,
            new InMemoryUpdateSnapshotStore(),
            time);

        var result = await coordinator.CheckAsync(UpdateCheckReason.Manual);

        Assert.Equal(time.GetUtcNow(), result.Snapshot.LastAttemptAt);
        Assert.Equal(time.GetUtcNow(), result.Snapshot.LastSuccessAt);
        time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1));
        Assert.False(coordinator.ShouldAutomaticallyCheck(result.Snapshot));
    }

    [Fact]
    public async Task FailedCheckPreservesRowsButCapturesCurrentSourceHealth()
    {
        var time = new ManualTimeProvider();
        var cachedUpdate = Update("Contoso.Tool", "winget", "1.5", "Contoso Tool");
        var fingerprint = new UpdateFingerprint
        {
            SourceId = "winget",
            PackageId = "contoso.tool",
            AvailableVersion = "1.5"
        };
        var previous = new UpdateSnapshot
        {
            LastAttemptAt = time.GetUtcNow().AddHours(-25),
            LastSuccessAt = time.GetUtcNow().AddHours(-25),
            Updates = [cachedUpdate],
            Fingerprints = [fingerprint],
            Sources =
            [
                new UpdateSourceHealthSnapshot
                {
                    Id = "winget",
                    Name = "winget",
                    Health = SourceHealth.Healthy
                }
            ]
        };
        var client = new FakeUpdateDiscoveryClient
        {
            GetUpdates = _ => Task.FromException<IReadOnlyList<PackageSummary>>(
                new IOException("Source is offline."))
        };
        var coordinator = new UpdateCoordinator(
            client,
            new InMemoryUpdateSnapshotStore(previous),
            time);

        var result = await coordinator.CheckAsync(
            UpdateCheckReason.Manual,
            [
                new PackageSourceStatus
                {
                    Id = "winget",
                    Name = "winget",
                    Health = SourceHealth.Unavailable,
                    Message = "Source is offline."
                }
            ]);

        Assert.Equal(UpdateCheckState.Failed, result.State);
        Assert.Same(cachedUpdate, Assert.Single(result.Snapshot.Updates));
        Assert.Same(fingerprint, Assert.Single(result.Snapshot.Fingerprints));
        var source = Assert.Single(result.Snapshot.Sources);
        Assert.Equal(SourceHealth.Unavailable, source.Health);
        Assert.Equal("Source is offline.", source.Message);
    }

    [Fact]
    public async Task FreshAutomaticCheckIsSkipped_ButManualCheckBypassesFreshness()
    {
        var time = new ManualTimeProvider();
        var snapshot = new UpdateSnapshot
        {
            LastAttemptAt = time.GetUtcNow().AddMinutes(-10),
            LastSuccessAt = time.GetUtcNow().AddMinutes(-10)
        };
        var client = new FakeUpdateDiscoveryClient();
        var coordinator = new UpdateCoordinator(
            client,
            new InMemoryUpdateSnapshotStore(snapshot),
            time);

        var automatic = await coordinator.CheckAsync(UpdateCheckReason.Automatic);
        var manual = await coordinator.CheckAsync(UpdateCheckReason.Manual);

        Assert.False(automatic.PerformedCheck);
        Assert.True(manual.PerformedCheck);
        Assert.Equal(1, client.UpdateCallCount);
    }

    [Fact]
    public void StateBecomesStaleAtTwentyFourHours()
    {
        var time = new ManualTimeProvider();
        var coordinator = new UpdateCoordinator(
            new FakeUpdateDiscoveryClient(),
            new InMemoryUpdateSnapshotStore(),
            time);
        var snapshot = new UpdateSnapshot
        {
            LastAttemptAt = time.GetUtcNow(),
            LastSuccessAt = time.GetUtcNow()
        };

        time.Advance(TimeSpan.FromHours(24));

        Assert.Equal(UpdateCheckState.Stale, coordinator.GetState(snapshot));
        Assert.True(coordinator.ShouldAutomaticallyCheck(snapshot));
    }

    [Fact]
    public async Task EverySixHours_BecomesEligibleAtSixHours()
    {
        var time = new ManualTimeProvider();
        var snapshot = new UpdateSnapshot
        {
            LastAttemptAt = time.GetUtcNow(),
            LastSuccessAt = time.GetUtcNow()
        };
        var client = new FakeUpdateDiscoveryClient();
        var coordinator = new UpdateCoordinator(
            client,
            new InMemoryUpdateSnapshotStore(snapshot),
            time);

        time.Advance(TimeSpan.FromHours(6) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(
            UpdateCheckState.Current,
            coordinator.GetState(snapshot, UpdateMonitoringCadence.EverySixHours));
        Assert.False(coordinator.ShouldAutomaticallyCheck(
            snapshot,
            UpdateMonitoringCadence.EverySixHours));

        time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await coordinator.CheckAsync(
            UpdateCheckReason.Automatic,
            cadence: UpdateMonitoringCadence.EverySixHours);

        Assert.True(result.PerformedCheck);
        Assert.Equal(1, client.UpdateCallCount);
    }

    [Fact]
    public async Task ManualCadence_DisablesAutomaticChecksButNotExplicitChecks()
    {
        var client = new FakeUpdateDiscoveryClient();
        var coordinator = new UpdateCoordinator(
            client,
            new InMemoryUpdateSnapshotStore());

        var automatic = await coordinator.CheckAsync(
            UpdateCheckReason.Automatic,
            cadence: UpdateMonitoringCadence.Manual);
        var manual = await coordinator.CheckAsync(
            UpdateCheckReason.Manual,
            cadence: UpdateMonitoringCadence.Manual);

        Assert.False(automatic.PerformedCheck);
        Assert.True(manual.PerformedCheck);
        Assert.Equal(1, client.UpdateCallCount);
    }

    [Theory]
    [InlineData(UpdateMonitoringCadence.Daily)]
    [InlineData(UpdateMonitoringCadence.EverySixHours)]
    public void AutomaticFailureRetryDelayRemainsOneHour(UpdateMonitoringCadence cadence)
    {
        var time = new ManualTimeProvider();
        var snapshot = new UpdateSnapshot
        {
            LastAttemptAt = time.GetUtcNow(),
            LastSuccessAt = time.GetUtcNow().AddHours(-25),
            LastError = "source unavailable"
        };
        var coordinator = new UpdateCoordinator(
            new FakeUpdateDiscoveryClient(),
            new InMemoryUpdateSnapshotStore(snapshot),
            time);

        Assert.False(coordinator.ShouldAutomaticallyCheck(snapshot, cadence));
        time.Advance(TimeSpan.FromHours(1) - TimeSpan.FromMilliseconds(1));
        Assert.False(coordinator.ShouldAutomaticallyCheck(snapshot, cadence));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(coordinator.ShouldAutomaticallyCheck(snapshot, cadence));
    }

    [Fact]
    public async Task CancellationDoesNotPersistAnAttempt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeUpdateDiscoveryClient
        {
            GetUpdates = async cancellationToken =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Array.Empty<PackageSummary>();
            }
        };
        var store = new InMemoryUpdateSnapshotStore();
        var coordinator = new UpdateCoordinator(client, store);
        using var cancellation = new CancellationTokenSource();

        var check = coordinator.CheckAsync(UpdateCheckReason.Manual, cancellationToken: cancellation.Token);
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneNativeCheck()
    {
        var nativeResult = new TaskCompletionSource<IReadOnlyList<PackageSummary>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeUpdateDiscoveryClient
        {
            GetUpdates = _ =>
            {
                entered.TrySetResult();
                return nativeResult.Task;
            }
        };
        var coordinator = new UpdateCoordinator(client, new InMemoryUpdateSnapshotStore());

        var first = coordinator.CheckAsync(UpdateCheckReason.Manual);
        await entered.Task;
        var second = coordinator.CheckAsync(UpdateCheckReason.Manual);
        nativeResult.SetResult([Update("Contoso.Tool", "winget", "2.0", "Contoso Tool")]);

        var results = await Task.WhenAll(first, second);
        Assert.Equal(1, client.UpdateCallCount);
        Assert.All(results, result => Assert.True(result.PerformedCheck));
        Assert.Same(results[0].Snapshot, results[1].Snapshot);
    }

    [Fact]
    public async Task ManualRequestConcurrentWithFreshAutomaticSkipStillPerformsCheck()
    {
        var time = new ManualTimeProvider();
        var snapshot = new UpdateSnapshot
        {
            LastAttemptAt = time.GetUtcNow(),
            LastSuccessAt = time.GetUtcNow()
        };
        var client = new FakeUpdateDiscoveryClient();
        var coordinator = new UpdateCoordinator(
            client,
            new InMemoryUpdateSnapshotStore(snapshot),
            time);

        var automatic = coordinator.CheckAsync(UpdateCheckReason.Automatic);
        var manual = coordinator.CheckAsync(UpdateCheckReason.Manual);
        var results = await Task.WhenAll(automatic, manual);

        Assert.False(results[0].PerformedCheck);
        Assert.True(results[1].PerformedCheck);
        Assert.Equal(1, client.UpdateCallCount);
    }

    private static PackageSummary Update(string id, string source, string version, string name) => new()
    {
        Key = new PackageKey(id, source),
        Name = name,
        SourceName = source,
        InstalledVersion = "1.0",
        AvailableVersion = version,
        Status = PackageStatus.UpdateAvailable
    };

    private sealed class ManualTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(Volatile.Read(ref _ticks));

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class InMemoryUpdateSnapshotStore(UpdateSnapshot? snapshot = null) : IUpdateSnapshotStore
    {
        private UpdateSnapshot? _snapshot = snapshot;

        public int SaveCount { get; private set; }

        public Task<UpdateSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task SaveAsync(UpdateSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _snapshot = snapshot;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUpdateDiscoveryClient : IUpdateDiscoveryClient
    {
        private int _updateCallCount;

        public int UpdateCallCount => Volatile.Read(ref _updateCallCount);

        public Func<CancellationToken, Task<IReadOnlyList<PackageSummary>>> GetUpdates { get; set; } =
            _ => Task.FromResult<IReadOnlyList<PackageSummary>>(Array.Empty<PackageSummary>());

        public Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _updateCallCount);
            return GetUpdates(cancellationToken);
        }
    }
}
