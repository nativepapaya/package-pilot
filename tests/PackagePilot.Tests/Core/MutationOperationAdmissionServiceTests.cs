using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class MutationOperationAdmissionServiceTests
{
    private const string BootSessionId = "test-boot-session";

    [Fact]
    public void Enqueue_OneHundredItemBatchSavesOnceBeforeQueuingAllOperations()
    {
        var admissions = CreateAdmissions(100);
        var queue = new BoundedOperationQueue(100);
        var tracker = new MutationVerificationTracker(BootSessionId);
        var store = new AtomicMutationVerificationStore();
        var service = new MutationOperationAdmissionService(queue, tracker, store);

        var result = service.Enqueue(admissions);

        Assert.Equal(100, result.OperationIds.Count);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Equal(admissions.Select(item => item.Operation.Id), result.OperationIds);
        Assert.Equal(admissions.Select(item => item.Operation.Id), queue.Enqueued.Select(item => item.Id));
        Assert.Equal(1, store.SaveAttemptCount);
        Assert.Single(store.SuccessfulSnapshots);
        Assert.Equal(100, tracker.Count);

        var durable = Assert.IsType<MutationVerificationSnapshot>(store.Load());
        Assert.Equal(100, durable.Markers.Count);
        Assert.All(
            durable.Markers,
            marker => Assert.Equal(MutationVerificationPhase.OutcomeUnknown, marker.Phase));
    }

    [Fact]
    public void Enqueue_PreAdmissionSaveFailureQueuesNothingAndRollsBackMarkers()
    {
        var admissions = CreateAdmissions(3);
        var queue = new BoundedOperationQueue(3);
        var tracker = new MutationVerificationTracker(BootSessionId);
        var store = new AtomicMutationVerificationStore(failingSaveAttempts: [1]);
        var service = new MutationOperationAdmissionService(queue, tracker, store);

        var exception = Assert.Throws<MutationRecoveryUnavailableException>(() =>
            service.Enqueue(admissions));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Empty(queue.Enqueued);
        Assert.Equal(0, tracker.Count);
        Assert.Empty(tracker.Export());
        Assert.Equal(1, store.SaveAttemptCount);
        Assert.Empty(store.SuccessfulSnapshots);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Enqueue_QueueRejectionWithSuccessfulRollbackKeepsOnlyAdmittedMarkers()
    {
        var admissions = CreateAdmissions(4);
        var queue = new BoundedOperationQueue(2);
        var tracker = new MutationVerificationTracker(BootSessionId);
        var store = new AtomicMutationVerificationStore();
        var service = new MutationOperationAdmissionService(queue, tracker, store);

        Assert.Throws<InvalidOperationException>(() => service.Enqueue(admissions));

        var admittedIds = admissions.Take(2).Select(item => item.Operation.Id).ToHashSet();
        Assert.Equal(2, queue.Enqueued.Count);
        Assert.True(admittedIds.SetEquals(queue.Enqueued.Select(item => item.Id)));
        Assert.Equal(2, tracker.Count);
        Assert.True(admittedIds.SetEquals(
            tracker.Export().Select(marker => marker.OperationId)));

        Assert.Equal(2, store.SaveAttemptCount);
        Assert.Equal(2, store.SuccessfulSnapshots.Count);
        Assert.Equal(4, store.SuccessfulSnapshots[0].Markers.Count);
        Assert.Equal(2, store.SuccessfulSnapshots[1].Markers.Count);
        var durable = Assert.IsType<MutationVerificationSnapshot>(store.Load());
        Assert.True(admittedIds.SetEquals(
            durable.Markers.Select(marker => marker.OperationId)));
        Assert.All(
            durable.Markers,
            marker => Assert.Equal(MutationVerificationPhase.OutcomeUnknown, marker.Phase));
    }

    [Fact]
    public void Enqueue_RollbackSaveFailureRestoresAllOutcomeUnknownMarkersAndFailsClosed()
    {
        var admissions = CreateAdmissions(4);
        var queue = new BoundedOperationQueue(2);
        var tracker = new MutationVerificationTracker(BootSessionId);
        var store = new AtomicMutationVerificationStore(failingSaveAttempts: [2]);
        var service = new MutationOperationAdmissionService(queue, tracker, store);

        var exception = Assert.Throws<MutationRecoveryUnavailableException>(() =>
            service.Enqueue(admissions));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(2, queue.Enqueued.Count);
        Assert.Equal(2, store.SaveAttemptCount);
        Assert.Single(store.SuccessfulSnapshots);

        var expectedIds = admissions.Select(item => item.Operation.Id).ToHashSet();
        var inMemoryMarkers = tracker.Export();
        Assert.Equal(4, tracker.Count);
        Assert.True(expectedIds.SetEquals(
            inMemoryMarkers.Select(marker => marker.OperationId)));
        Assert.All(
            inMemoryMarkers,
            marker => Assert.Equal(MutationVerificationPhase.OutcomeUnknown, marker.Phase));

        var durable = Assert.IsType<MutationVerificationSnapshot>(store.Load());
        Assert.True(expectedIds.SetEquals(
            durable.Markers.Select(marker => marker.OperationId)));
        Assert.All(
            durable.Markers,
            marker => Assert.Equal(MutationVerificationPhase.OutcomeUnknown, marker.Phase));
    }

    private static MutationAdmission[] CreateAdmissions(int count) =>
        Enumerable.Range(0, count)
            .Select(index =>
            {
                var package = new PackageSummary
                {
                    Key = new PackageKey($"Contoso.Package.{index:D3}", "winget"),
                    Name = $"Contoso Package {index:D3}",
                    SourceName = "winget",
                    InstalledVersion = "1.0.0",
                    AvailableVersion = "2.0.0",
                    Status = PackageStatus.UpdateAvailable
                };
                var operation = PackageOperation.Create(
                    PackageOperationKind.Upgrade,
                    package.Key,
                    package.Name) with
                {
                    EnqueuedAt = new DateTimeOffset(
                        2026,
                        7,
                        15,
                        12,
                        0,
                        0,
                        TimeSpan.Zero).AddSeconds(index)
                };
                return new MutationAdmission(operation, package);
            })
            .ToArray();

    private sealed class BoundedOperationQueue(int capacity) : IOperationQueue
    {
        private readonly List<PackageOperation> _enqueued = [];

        public event EventHandler<OperationQueueChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task Initialization => Task.CompletedTask;

        public IReadOnlyList<PackageOperation> Enqueued => _enqueued;

        public OperationQueueSnapshot Snapshot => new()
        {
            Pending = _enqueued
                .Select(operation => new OperationQueueEntry(
                    operation,
                    new OperationProgress
                    {
                        OperationId = operation.Id,
                        State = PackageOperationState.Queued
                    }))
                .ToArray()
        };

        public Guid Enqueue(
            PackageOperation operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_enqueued.Count >= capacity)
            {
                throw new InvalidOperationException(
                    $"The bounded test queue accepts only {capacity} operations.");
            }

            _enqueued.Add(operation);
            return operation.Id;
        }

        public bool TryCancel(Guid operationId) => false;

        public bool TryBeginShutdownIfIdle() => _enqueued.Count == 0;

        public void ClearHistory()
        {
        }

        public Task WaitForIdleAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AtomicMutationVerificationStore(
        IReadOnlyCollection<int>? failingSaveAttempts = null) : IMutationVerificationStore
    {
        private readonly HashSet<int> _failingSaveAttempts =
            failingSaveAttempts?.ToHashSet() ?? [];
        private readonly List<MutationVerificationSnapshot> _successfulSnapshots = [];
        private MutationVerificationSnapshot? _durableSnapshot;

        public int SaveAttemptCount { get; private set; }

        public IReadOnlyList<MutationVerificationSnapshot> SuccessfulSnapshots =>
            _successfulSnapshots;

        public MutationVerificationSnapshot? Load() =>
            _durableSnapshot is null ? null : Clone(_durableSnapshot);

        public void Save(MutationVerificationSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            snapshot.Validate();
            SaveAttemptCount++;
            if (_failingSaveAttempts.Contains(SaveAttemptCount))
            {
                throw new IOException($"Atomic save {SaveAttemptCount} failed for the test.");
            }

            var committed = Clone(snapshot);
            _durableSnapshot = committed;
            _successfulSnapshots.Add(Clone(committed));
        }

        private static MutationVerificationSnapshot Clone(
            MutationVerificationSnapshot snapshot) => snapshot with
            {
                Markers = snapshot.Markers
                .Select(marker => marker with
                {
                    Package = marker.Package with { }
                })
                .ToArray(),
                VerifiedOperationIds = snapshot.VerifiedOperationIds.ToArray()
            };
    }
}
