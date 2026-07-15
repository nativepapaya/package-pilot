using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class UpdateScanWorkerTests
{
    [Fact]
    public async Task BackgroundScanNotifiesOnlyForNewFingerprints()
    {
        var previous = Snapshot(Fingerprint("1.0"));
        var current = Snapshot(Fingerprint("2.0"));
        var coordinator = new StubCoordinator(previous, new UpdateCheckResult
        {
            Snapshot = current,
            State = UpdateCheckState.Current,
            PerformedCheck = true
        });
        var sink = new RecordingNotificationSink();
        var worker = CreateWorker(coordinator, sink);

        var result = await worker.RunAsync(
            UpdateCheckReason.Automatic,
            isForegroundWindowActive: false);

        Assert.True(result.NotificationApplied);
        Assert.True(result.Notification.ShowOrReplaceNotification);
        Assert.Equal(1, result.Notification.BadgeCount);
        Assert.Equal("2.0", Assert.Single(result.Notification.AddedUpdates).AvailableVersion);
        Assert.Same(result.Notification, Assert.Single(sink.Decisions));
    }

    [Fact]
    public async Task ForegroundScanSuppressesToastButStillUpdatesBadge()
    {
        var current = Snapshot(Fingerprint("2.0"));
        var coordinator = new StubCoordinator(null, new UpdateCheckResult
        {
            Snapshot = current,
            State = UpdateCheckState.Current,
            PerformedCheck = true
        });
        var sink = new RecordingNotificationSink();

        var result = await CreateWorker(coordinator, sink).RunAsync(
            UpdateCheckReason.Manual,
            isForegroundWindowActive: true);

        Assert.False(result.Notification.ShowOrReplaceNotification);
        Assert.Equal(1, result.Notification.BadgeCount);
    }

    [Fact]
    public async Task ForegroundActivityIsEvaluatedAfterDiscoveryCompletes()
    {
        var previous = Snapshot(Fingerprint("1.0"));
        var current = Snapshot(Fingerprint("2.0"));
        var activity = new MutableWindowActivityService(
            new WindowActivityState(IsVisible: false, IsActive: false));
        var coordinator = new StubCoordinator(previous, new UpdateCheckResult
        {
            Snapshot = current,
            State = UpdateCheckState.Current,
            PerformedCheck = true
        })
        {
            OnCheck = () => activity.Set(
                new WindowActivityState(IsVisible: true, IsActive: true))
        };
        var sink = new RecordingNotificationSink();

        var result = await CreateWorker(coordinator, sink).RunAsync(
            UpdateCheckReason.Automatic,
            activity);

        Assert.False(result.Notification.ShowOrReplaceNotification);
        Assert.Equal(1, result.Notification.BadgeCount);
    }

    [Fact]
    public async Task FailedScanPreservesPreviousBadgeAndRequestsNoToast()
    {
        var previous = Snapshot(Fingerprint("1.0"));
        var failed = previous with
        {
            LastAttemptAt = DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
            LastError = "COM activation failed"
        };
        var coordinator = new StubCoordinator(previous, new UpdateCheckResult
        {
            Snapshot = failed,
            State = UpdateCheckState.Failed,
            PerformedCheck = true
        });

        var result = await CreateWorker(coordinator, new RecordingNotificationSink()).RunAsync(
            UpdateCheckReason.Automatic,
            isForegroundWindowActive: false);

        Assert.Equal(1, result.Notification.BadgeCount);
        Assert.False(result.Notification.ShowOrReplaceNotification);
        Assert.False(result.Notification.ClearNotification);
    }

    [Theory]
    [InlineData(UpdateCheckState.NotChecked)]
    [InlineData(UpdateCheckState.Stale)]
    public async Task NonAuthoritativeSkippedScanNeverClearsBadgeOrNotification(
        UpdateCheckState state)
    {
        var previous = Snapshot(Fingerprint("1.0"));
        var nonAuthoritative = new UpdateSnapshot
        {
            LastAttemptAt = previous.LastAttemptAt,
            LastSuccessAt = state == UpdateCheckState.Stale
                ? previous.LastSuccessAt
                : null
        };
        var coordinator = new StubCoordinator(previous, new UpdateCheckResult
        {
            Snapshot = nonAuthoritative,
            State = state,
            PerformedCheck = false
        });
        var sink = new RecordingNotificationSink();

        var result = await CreateWorker(coordinator, sink).RunAsync(
            UpdateCheckReason.Automatic,
            isForegroundWindowActive: false,
            cadence: UpdateMonitoringCadence.Manual);

        Assert.Equal(1, result.Notification.BadgeCount);
        Assert.False(result.Notification.ClearNotification);
        Assert.False(result.Notification.ShowOrReplaceNotification);
        Assert.Same(result.Notification, Assert.Single(sink.Decisions));
    }

    [Fact]
    public async Task NotificationFailureDoesNotChangeSuccessfulCheckResult()
    {
        var current = Snapshot(Fingerprint("2.0"));
        var coordinator = new StubCoordinator(null, new UpdateCheckResult
        {
            Snapshot = current,
            State = UpdateCheckState.Current,
            PerformedCheck = true
        });

        var result = await CreateWorker(
            coordinator,
            new ThrowingNotificationSink()).RunAsync(
                UpdateCheckReason.Automatic,
                isForegroundWindowActive: false);

        Assert.Equal(UpdateCheckState.Current, result.Check.State);
        Assert.False(result.NotificationApplied);
        Assert.Equal("notification unavailable", result.NotificationError);
    }

    [Fact]
    public async Task NamedMutexSerializesIndependentWorkers()
    {
        var mutexName = $@"Local\PackagePilot.Tests.{Guid.NewGuid():N}";
        var firstGate = new CrossProcessUpdateScanMutex(mutexName);
        var secondGate = new CrossProcessUpdateScanMutex(mutexName);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = firstGate.RunExclusiveAsync(async token =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token);
            return 1;
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = secondGate.RunExclusiveAsync(token =>
        {
            secondEntered.SetResult();
            return Task.FromResult(2);
        });

        await Task.Delay(100);
        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.SetResult();

        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(secondEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task AutomaticScanTimesOutAsDistinctBusySkipAndRetainsCachedBadge()
    {
        var mutexName = $@"Local\PackagePilot.Tests.{Guid.NewGuid():N}";
        var firstGate = new CrossProcessUpdateScanMutex(mutexName);
        var secondGate = new CrossProcessUpdateScanMutex(mutexName);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = firstGate.RunExclusiveAsync(async token =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token);
            return 1;
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var previous = Snapshot(Fingerprint("1.0"));
        var coordinator = new StubCoordinator(previous, new UpdateCheckResult
        {
            Snapshot = previous,
            State = UpdateCheckState.Stale,
            PerformedCheck = false
        });
        var worker = new UpdateScanWorker(
            coordinator,
            new UpdateNotificationPolicy(),
            new RecordingNotificationSink(),
            secondGate,
            TimeSpan.FromMilliseconds(50));

        var result = await worker.RunAsync(
            UpdateCheckReason.Automatic,
            isForegroundWindowActive: false);

        Assert.Equal(UpdateScanExecutionState.SkippedBusy, result.State);
        Assert.False(result.Check.PerformedCheck);
        Assert.Equal(1, result.Notification.BadgeCount);
        Assert.False(result.Notification.ShowOrReplaceNotification);

        releaseFirst.SetResult();
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ManualScanWaitRemainsCancellable()
    {
        var mutexName = $@"Local\PackagePilot.Tests.{Guid.NewGuid():N}";
        var firstGate = new CrossProcessUpdateScanMutex(mutexName);
        var secondGate = new CrossProcessUpdateScanMutex(mutexName);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = firstGate.RunExclusiveAsync(async token =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token);
            return 1;
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var worker = new UpdateScanWorker(
            new StubCoordinator(null, new UpdateCheckResult()),
            new UpdateNotificationPolicy(),
            new RecordingNotificationSink(),
            secondGate,
            TimeSpan.FromMilliseconds(25));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunAsync(
            UpdateCheckReason.Manual,
            isForegroundWindowActive: true,
            cancellationToken: cancellation.Token));

        releaseFirst.SetResult();
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task BackgroundRunnerRecordsForegroundFallbackAfterActivationFailure()
    {
        var failed = Snapshot(Fingerprint("1.0")) with
        {
            LastError = "WinGet COM activation failed"
        };
        var coordinator = new StubCoordinator(failed, new UpdateCheckResult
        {
            Snapshot = failed,
            State = UpdateCheckState.Failed,
            PerformedCheck = true
        });
        var statusStore = new InMemoryBackgroundStatusStore();
        var runner = new BackgroundUpdateRunner(
            CreateWorker(coordinator, new RecordingNotificationSink()),
            statusStore,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00Z")));

        var status = await runner.RunOnceAsync();

        Assert.Equal(BackgroundUpdateRunState.Failed, status.State);
        Assert.True(status.ForegroundFallbackRequired);
        Assert.Equal("WinGet COM activation failed", status.Message);
        Assert.Equal(status, statusStore.Value);
    }

    [Fact]
    public async Task BackgroundRunnerManualCadenceSkipsWithoutInvokingDiscovery()
    {
        var coordinator = new StubCoordinator(null, new UpdateCheckResult());
        var statusStore = new InMemoryBackgroundStatusStore();
        var runner = new BackgroundUpdateRunner(
            CreateWorker(coordinator, new RecordingNotificationSink()),
            statusStore,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00Z")),
            UpdateMonitoringCadence.Manual);

        var status = await runner.RunOnceAsync();

        Assert.Equal(BackgroundUpdateRunState.Skipped, status.State);
        Assert.Equal(0, coordinator.CheckCallCount);
        Assert.Contains("Manual", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundRunnerRecordsDistinctBusyStatus()
    {
        var mutexName = $@"Local\PackagePilot.Tests.{Guid.NewGuid():N}";
        var firstGate = new CrossProcessUpdateScanMutex(mutexName);
        var secondGate = new CrossProcessUpdateScanMutex(mutexName);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = firstGate.RunExclusiveAsync(async token =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token);
            return 1;
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var previous = Snapshot(Fingerprint("1.0"));
            var coordinator = new StubCoordinator(previous, new UpdateCheckResult
            {
                Snapshot = previous,
                State = UpdateCheckState.Stale,
                PerformedCheck = false
            });
            var worker = new UpdateScanWorker(
                coordinator,
                new UpdateNotificationPolicy(),
                new RecordingNotificationSink(),
                secondGate,
                TimeSpan.FromMilliseconds(50));
            var runner = new BackgroundUpdateRunner(
                worker,
                new InMemoryBackgroundStatusStore());

            var status = await runner.RunOnceAsync();

            Assert.Equal(BackgroundUpdateRunState.SkippedBusy, status.State);
            Assert.False(status.ForegroundFallbackRequired);
            Assert.Equal(1, status.UpdateCount);
        }
        finally
        {
            releaseFirst.TrySetResult();
            Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task BackgroundRunnerRetriesNotificationAfterTransientShowFailure()
    {
        var current = Snapshot(Fingerprint("2.0"));
        var coordinator = new PersistingCoordinator(current);
        var sink = new ThrowOnceNotificationSink();
        var statusStore = new InMemoryBackgroundStatusStore();
        var runner = new BackgroundUpdateRunner(
            CreateWorker(coordinator, sink),
            statusStore,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00Z")));

        var first = await runner.RunOnceAsync();

        Assert.Equal(BackgroundUpdateRunState.Completed, first.State);
        Assert.True(first.NotificationRetryPending);
        Assert.True(first.ForegroundFallbackRequired);
        Assert.Equal("notification unavailable", first.Message);
        Assert.Equal(1, sink.ShowAttempts);

        var second = await runner.RunOnceAsync();

        Assert.Equal(BackgroundUpdateRunState.Completed, second.State);
        Assert.False(second.NotificationRetryPending);
        Assert.False(second.ForegroundFallbackRequired);
        Assert.Null(second.Message);
        Assert.Equal(2, sink.ShowAttempts);
    }

    [Fact]
    public async Task SuccessfulNotificationRetryClearsPendingFlagWhenFreshScanIsSkipped()
    {
        var current = Snapshot(Fingerprint("2.0"));
        var coordinator = new FirstPerformedThenFreshCoordinator(current);
        var sink = new ThrowOnceNotificationSink();
        var statusStore = new InMemoryBackgroundStatusStore();
        var runner = new BackgroundUpdateRunner(
            CreateWorker(coordinator, sink),
            statusStore,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-14T12:00:00Z")));

        var first = await runner.RunOnceAsync();
        var second = await runner.RunOnceAsync();

        Assert.Equal(BackgroundUpdateRunState.Completed, first.State);
        Assert.True(first.NotificationRetryPending);
        Assert.Equal(BackgroundUpdateRunState.Skipped, second.State);
        Assert.False(second.NotificationRetryPending);
        Assert.False(second.ForegroundFallbackRequired);
        Assert.Equal(2, sink.ShowAttempts);
    }

    private static UpdateScanWorker CreateWorker(
        IUpdateCoordinator coordinator,
        IUpdateNotificationSink sink) => new(
            coordinator,
            new UpdateNotificationPolicy(),
            sink,
            new CrossProcessUpdateScanMutex($@"Local\PackagePilot.Tests.{Guid.NewGuid():N}"));

    private static UpdateSnapshot Snapshot(params UpdateFingerprint[] fingerprints) => new()
    {
        LastAttemptAt = DateTimeOffset.Parse("2026-07-14T10:00:00Z"),
        LastSuccessAt = DateTimeOffset.Parse("2026-07-14T10:00:00Z"),
        Fingerprints = fingerprints
    };

    private static UpdateFingerprint Fingerprint(string version) => new()
    {
        SourceId = "winget",
        PackageId = "Contoso.App",
        AvailableVersion = version
    };

    private sealed class StubCoordinator(
        UpdateSnapshot? previous,
        UpdateCheckResult result) : IUpdateCoordinator
    {
        private int _checkCallCount;

        public int CheckCallCount => Volatile.Read(ref _checkCallCount);
        public Action? OnCheck { get; init; }

        public Task<UpdateSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(previous);

        public UpdateCheckState GetState(
            UpdateSnapshot? snapshot,
            UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily) => result.State;

        public bool ShouldAutomaticallyCheck(
            UpdateSnapshot? snapshot,
            UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily) =>
            cadence != UpdateMonitoringCadence.Manual;

        public Task<UpdateCheckResult> CheckAsync(
            UpdateCheckReason reason,
            IReadOnlyList<PackageSourceStatus>? sourceStatuses = null,
            CancellationToken cancellationToken = default,
            UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily)
        {
            Interlocked.Increment(ref _checkCallCount);
            OnCheck?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class PersistingCoordinator(UpdateSnapshot current) : IUpdateCoordinator
    {
        private UpdateSnapshot? _previous;

        public Task<UpdateSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_previous);

        public UpdateCheckState GetState(
            UpdateSnapshot? snapshot,
            UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily) =>
            UpdateCheckState.Current;

        public bool ShouldAutomaticallyCheck(
            UpdateSnapshot? snapshot,
            UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily) => true;

        public Task<UpdateCheckResult> CheckAsync(
            UpdateCheckReason reason,
            IReadOnlyList<PackageSourceStatus>? sourceStatuses = null,
            CancellationToken cancellationToken = default,
            UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily)
        {
            _previous = current;
            return Task.FromResult(new UpdateCheckResult
            {
                Snapshot = current,
                State = UpdateCheckState.Current,
                PerformedCheck = true
            });
        }
    }

    private sealed class FirstPerformedThenFreshCoordinator(UpdateSnapshot current)
        : IUpdateCoordinator
    {
        private UpdateSnapshot? _previous;
        private int _checkCount;

        public Task<UpdateSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_previous);

        public UpdateCheckState GetState(
            UpdateSnapshot? snapshot,
            UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily) =>
            UpdateCheckState.Current;

        public bool ShouldAutomaticallyCheck(
            UpdateSnapshot? snapshot,
            UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily) => true;

        public Task<UpdateCheckResult> CheckAsync(
            UpdateCheckReason reason,
            IReadOnlyList<PackageSourceStatus>? sourceStatuses = null,
            CancellationToken cancellationToken = default,
            UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily)
        {
            _previous = current;
            return Task.FromResult(new UpdateCheckResult
            {
                Snapshot = current,
                State = UpdateCheckState.Current,
                PerformedCheck = Interlocked.Increment(ref _checkCount) == 1
            });
        }
    }

    private sealed class MutableWindowActivityService(WindowActivityState current)
        : IWindowActivityService
    {
        public event EventHandler<WindowActivityChangedEventArgs>? ActivityChanged;

        public WindowActivityState Current { get; private set; } = current;

        public void Set(WindowActivityState state)
        {
            Current = state;
            ActivityChanged?.Invoke(this, new WindowActivityChangedEventArgs(state));
        }
    }

    private sealed class RecordingNotificationSink : IUpdateNotificationSink
    {
        public List<UpdateNotificationDecision> Decisions { get; } = [];

        public Task ApplyAsync(
            UpdateNotificationDecision decision,
            CancellationToken cancellationToken = default)
        {
            Decisions.Add(decision);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotificationSink : IUpdateNotificationSink
    {
        public Task ApplyAsync(
            UpdateNotificationDecision decision,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("notification unavailable"));
    }

    private sealed class ThrowOnceNotificationSink : IUpdateNotificationSink
    {
        private int _showAttempts;

        public int ShowAttempts => Volatile.Read(ref _showAttempts);

        public Task ApplyAsync(
            UpdateNotificationDecision decision,
            CancellationToken cancellationToken = default)
        {
            if (!decision.ShowOrReplaceNotification)
            {
                return Task.CompletedTask;
            }

            if (Interlocked.Increment(ref _showAttempts) == 1)
            {
                return Task.FromException(
                    new InvalidOperationException("notification unavailable"));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryBackgroundStatusStore : IBackgroundUpdateRunStatusStore
    {
        public BackgroundUpdateRunStatus? Value { get; private set; }

        public Task<BackgroundUpdateRunStatus?> LoadAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Value);

        public Task SaveAsync(
            BackgroundUpdateRunStatus status,
            CancellationToken cancellationToken = default)
        {
            Value = status;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
