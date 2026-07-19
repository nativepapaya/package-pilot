using System.Runtime.InteropServices;
using System.Threading.Channels;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Executes package operations one at a time so installers and elevation prompts cannot overlap.
/// </summary>
public sealed class OperationQueue : IOperationQueue
{
    private const int MaximumHistory = 100;
    private static readonly TimeSpan ProgressNotificationInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly object _persistenceScheduleGate = new();
    private readonly IWingetClient _wingetClient;
    private readonly IMsixPackageOperationClient? _msixClient;
    private readonly IPrivilegedPackageOperationBroker? _privilegedPackageOperationBroker;
    private readonly IOperationHistoryStore? _historyStore;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<QueueItem> _channel;
    private readonly List<QueueItem> _pending = [];
    private readonly List<OperationResult> _history = [];
    private readonly Task _worker;
    private TaskCompletionSource _idle = CompletedSource();
    private QueueItem? _current;
    private Exception? _lastPersistenceError;
    private Task _persistenceTail = Task.CompletedTask;
    private bool _historyWasCleared;
    private bool _acceptingOperations = true;
    private bool _disposed;

    public OperationQueue(
        IWingetClient wingetClient,
        IOperationHistoryStore? historyStore = null,
        TimeProvider? timeProvider = null,
        IMsixPackageOperationClient? msixClient = null,
        IPrivilegedPackageOperationBroker? privilegedPackageOperationBroker = null)
    {
        _wingetClient = wingetClient ?? throw new ArgumentNullException(nameof(wingetClient));
        _historyStore = historyStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _msixClient = msixClient;
        _privilegedPackageOperationBroker = privilegedPackageOperationBroker;
        _channel = Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        Initialization = LoadHistoryAsync();
        _worker = ProcessQueueAsync();
    }

    public event EventHandler<OperationQueueChangedEventArgs>? Changed;

    public Task Initialization { get; }

    public OperationQueueSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshotLocked();
            }
        }
    }

    public Guid Enqueue(PackageOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (operation.Id == Guid.Empty)
        {
            throw new ArgumentException("An operation must have a non-empty ID.", nameof(operation));
        }

        if (operation.EffectiveTarget is null || string.IsNullOrWhiteSpace(operation.EffectiveTarget.Id))
        {
            throw new ArgumentException("An operation must identify a package.", nameof(operation));
        }

        // The external token is routed through TryCancel rather than linked directly. That keeps
        // cancellation from reaching WinGet after an installer has crossed the cancellation boundary.
        var item = new QueueItem(operation, new CancellationTokenSource());
        if (cancellationToken.CanBeCanceled)
        {
            item.ExternalCancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var registration = (CancellationRegistrationState)state!;
                    registration.Queue.TryCancel(registration.OperationId);
                },
                new CancellationRegistrationState(this, operation.Id));
        }

        OperationQueueSnapshot snapshot;

        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_acceptingOperations)
                {
                    throw new InvalidOperationException("The operation queue is shutting down and no longer accepts work.");
                }

                if ((_current?.Operation.Id == operation.Id)
                    || _pending.Any(candidate => candidate.Operation.Id == operation.Id))
                {
                    throw new ArgumentException($"Operation '{operation.Id}' is already queued.", nameof(operation));
                }

                var duplicate = _current is not null
                    && IsEquivalentActiveOperation(_current.Operation, operation)
                        ? _current
                        : _pending.FirstOrDefault(candidate =>
                            IsEquivalentActiveOperation(candidate.Operation, operation));
                if (duplicate is not null)
                {
                    throw new DuplicatePackageOperationException(
                        duplicate.Operation.Id,
                        operation.EffectiveTarget!.Id);
                }

                if (_pending.Count == 0 && _current is null)
                {
                    _idle = NewSource();
                }

                _pending.Add(item);
                if (!_channel.Writer.TryWrite(item))
                {
                    _pending.Remove(item);
                    throw new InvalidOperationException("The operation queue is no longer accepting work.");
                }

                snapshot = CreateSnapshotLocked();
            }
        }
        catch
        {
            // External cancellation callbacks call back into TryCancel and need _gate. Dispose
            // only after the lock has been released so rejected work cannot deadlock cancellation.
            item.Dispose();
            throw;
        }

        RaiseChanged(snapshot);

        if (cancellationToken.IsCancellationRequested)
        {
            TryCancel(operation.Id);
        }

        return operation.Id;
    }

    public bool TryBeginShutdownIfIdle()
    {
        lock (_gate)
        {
            if (_disposed || !_acceptingOperations)
            {
                return true;
            }

            if (_pending.Count > 0 || _current is not null)
            {
                return false;
            }

            _acceptingOperations = false;
            return true;
        }
    }

    public bool TryCancel(Guid operationId)
    {
        QueueItem? item;
        OperationQueueSnapshot? snapshot = null;

        lock (_gate)
        {
            item = _pending.FirstOrDefault(candidate => candidate.Operation.Id == operationId);
            if (item is not null)
            {
                if (item.IsFinalized)
                {
                    return false;
                }

                item.IsFinalized = true;
                _pending.Remove(item);
                item.Progress = NewProgress(item.Operation, PackageOperationState.Cancelled, "Cancelled before starting.");
                AddHistoryLocked(CreateCancelledResult(item.Operation, _timeProvider.GetUtcNow()));
                snapshot = CreateSnapshotLocked();
            }
            else if (_current?.Operation.Id == operationId)
            {
                item = _current;
                if (item.IsFinalized || !item.Progress.CanCancel)
                {
                    return false;
                }

                item.Progress = item.Progress with
                {
                    Message = "Cancellation requested…",
                    Timestamp = _timeProvider.GetUtcNow()
                };
                snapshot = CreateSnapshotLocked();
            }
            else
            {
                return false;
            }
        }

        item.RequestCancellation();
        RaiseChanged(snapshot!);
        return true;
    }

    public Task<bool> TryMarkUpgradeNoChangeDetectedAsync(Guid operationId, PackageKey package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (operationId == Guid.Empty || package.IsEmpty)
        {
            return Task.FromResult(false);
        }

        // Keep the history rewrite and its persistence attempt in the same serialized tail.
        // A later ClearHistory/save can run only after this transaction has committed or rolled
        // back, so the caller never removes its recovery marker based on an in-memory-only row.
        lock (_persistenceScheduleGate)
        {
            OperationResult replacement;
            OperationResult[] originalHistory;
            OperationResult[] mutatedHistory;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_historyStore is null)
                {
                    return Task.FromResult(false);
                }

                var index = _history.FindIndex(result =>
                    result.OperationId == operationId
                    && result.Kind == PackageOperationKind.Upgrade
                    && result.State == PackageOperationState.Completed
                    && result.EffectiveTarget is WingetTarget target
                    && target.Package == package);
                if (index < 0)
                {
                    // A durable recovery marker can outlive the bounded/cleared Activity list.
                    // Refuse conflicting identities, but reconstruct the exact failed row when
                    // no result for this operation survives so the marker can be released safely.
                    if (_history.Any(result => result.OperationId == operationId))
                    {
                        return Task.FromResult(false);
                    }
                }

                originalHistory = _history.ToArray();
                var completedAt = _timeProvider.GetUtcNow();
                replacement = index >= 0
                    ? _history[index] with
                    {
                        State = PackageOperationState.Failed,
                        RebootRequired = false,
                        Error = CreateNoChangeDetectedError()
                    }
                    : new OperationResult
                    {
                        OperationId = operationId,
                        Package = package,
                        Target = new WingetTarget { Package = package },
                        Kind = PackageOperationKind.Upgrade,
                        State = PackageOperationState.Failed,
                        StartedAt = completedAt,
                        CompletedAt = completedAt,
                        Error = CreateNoChangeDetectedError()
                    };
                if (index >= 0)
                {
                    _history[index] = replacement;
                }
                else
                {
                    AddHistoryLocked(replacement);
                }
                mutatedHistory = _history.ToArray();
            }

            var previous = _persistenceTail;
            var transaction = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await Initialization.ConfigureAwait(false);
                var persisted = await PersistHistorySafeAsync(publishFailure: false)
                    .ConfigureAwait(false);

                OperationQueueSnapshot snapshot;
                var committed = false;
                lock (_gate)
                {
                    var currentIndex = _history.FindIndex(result =>
                        result.OperationId == operationId
                        && result.EffectiveTarget is WingetTarget target
                        && target.Package == package);
                    var replacementStillCurrent = currentIndex >= 0
                        && _history[currentIndex] == replacement;

                    committed = persisted && replacementStillCurrent;
                    if (!persisted && _history.SequenceEqual(mutatedHistory))
                    {
                        _history.Clear();
                        _history.AddRange(originalHistory);
                    }

                    snapshot = CreateSnapshotLocked();
                }

                RaiseChanged(snapshot);
                return committed;
            });
            _persistenceTail = transaction;
            return transaction;
        }
    }

    public void ClearHistory()
    {
        OperationQueueSnapshot snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _history.Clear();
            _historyWasCleared = true;
            snapshot = CreateSnapshotLocked();
        }

        RaiseChanged(snapshot);
        _ = ScheduleHistoryPersistence();
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            Task waitTask;
            lock (_gate)
            {
                if (_pending.Count == 0 && _current is null && _idle.Task.IsCompleted)
                {
                    return;
                }

                waitTask = _idle.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (_pending.Count == 0 && _current is null)
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Guid> cancellableIds;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _acceptingOperations = false;
            cancellableIds = _pending.Select(item => item.Operation.Id).ToList();
            if (_current?.Progress.CanCancel == true)
            {
                cancellableIds.Add(_current.Operation.Id);
            }

            _channel.Writer.TryComplete();
        }

        foreach (var id in cancellableIds)
        {
            TryCancel(id);
        }

        await _worker.ConfigureAwait(false);

        Task persistence;
        lock (_persistenceScheduleGate)
        {
            persistence = _persistenceTail;
        }

        await persistence.ConfigureAwait(false);
    }

    private async Task LoadHistoryAsync()
    {
        if (_historyStore is null)
        {
            return;
        }

        try
        {
            var loaded = await _historyStore.LoadAsync().ConfigureAwait(false);
            OperationQueueSnapshot snapshot;
            lock (_gate)
            {
                // Results completed during startup are newer than anything loaded from disk.
                var existingIds = _history.Select(item => item.OperationId).ToHashSet();
                if (!_historyWasCleared)
                {
                    _history.AddRange(loaded.Where(item => existingIds.Add(item.OperationId)));
                }
                TrimHistoryLocked();
                _lastPersistenceError = null;
                snapshot = CreateSnapshotLocked();
            }

            RaiseChanged(snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate)
            {
                _lastPersistenceError = exception;
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        await Initialization.ConfigureAwait(false);

        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            bool wasFinalized;
            DateTimeOffset startedAt;
            OperationQueueSnapshot? snapshot = null;
            lock (_gate)
            {
                wasFinalized = item.IsFinalized;
                if (wasFinalized)
                {
                    startedAt = default;
                }
                else
                {
                    _pending.Remove(item);

                    if (item.Cancellation.IsCancellationRequested)
                    {
                        item.IsFinalized = true;
                        wasFinalized = true;
                        var now = _timeProvider.GetUtcNow();
                        item.Progress = NewProgress(item.Operation, PackageOperationState.Cancelled, "Cancelled before starting.");
                        AddHistoryLocked(CreateCancelledResult(item.Operation, now));
                        snapshot = CreateSnapshotLocked();
                        startedAt = default;
                    }
                    else
                    {
                        _current = item;
                        startedAt = _timeProvider.GetUtcNow();
                        item.StartedAt = startedAt;
                        item.Progress = NewProgress(
                            item.Operation,
                            PackageOperationState.Resolving,
                            item.Operation.RunAsAdministrator
                                ? "Preparing administrator approval (UAC)..."
                                : "Resolving package…");
                        snapshot = CreateSnapshotLocked();
                    }
                }
            }

            if (snapshot is not null)
            {
                RaiseChanged(snapshot);
            }

            if (wasFinalized)
            {
                await ScheduleHistoryPersistence().ConfigureAwait(false);
                item.Dispose();
                CompleteIdleIfAppropriate();
                continue;
            }

            OperationResult result;
            try
            {
                var progress = new InlineProgress<OperationProgress>(value => ReportProgress(item, value));
                result = item.Operation.RunAsAdministrator
                    ? await ExecutePrivilegedOperationAsync(item, progress).ConfigureAwait(false)
                    : item.Operation.EffectiveTarget switch
                    {
                        MsixTarget => await ExecuteMsixOperationAsync(item, progress).ConfigureAwait(false),
                        WingetTarget => item.Operation.Kind switch
                        {
                            PackageOperationKind.Install => await _wingetClient.InstallAsync(
                                item.Operation, progress, item.Cancellation.Token).ConfigureAwait(false),
                            PackageOperationKind.Upgrade => await _wingetClient.UpgradeAsync(
                                item.Operation, progress, item.Cancellation.Token).ConfigureAwait(false),
                            PackageOperationKind.Uninstall => await _wingetClient.UninstallAsync(
                                item.Operation, progress, item.Cancellation.Token).ConfigureAwait(false),
                            _ => throw new InvalidOperationException($"Unsupported operation kind '{item.Operation.Kind}'.")
                        },
                        _ => throw new InvalidOperationException("The package operation target is not supported.")
                    };

                result = NormalizeResult(item.Operation, result, startedAt, _timeProvider.GetUtcNow());
            }
            catch (OperationCanceledException) when (item.Cancellation.IsCancellationRequested)
            {
                result = CreateCancelledResult(item.Operation, _timeProvider.GetUtcNow(), startedAt);
            }
            catch (WingetException exception)
            {
                result = CreateFailedResult(item.Operation, exception.Error, startedAt);
            }
            catch (Exception exception)
            {
                result = CreateFailedResult(item.Operation, CreateError(exception), startedAt);
            }

            lock (_gate)
            {
                item.IsFinalized = true;
                item.Progress = NewProgress(item.Operation, result.State, result.Error?.Message);
                _current = null;
                AddHistoryLocked(result);
                snapshot = CreateSnapshotLocked();
            }

            RaiseChanged(snapshot);
            await ScheduleHistoryPersistence().ConfigureAwait(false);
            item.Dispose();
            CompleteIdleIfAppropriate();
        }

        CompleteIdleIfAppropriate(force: true);
    }

    private void ReportProgress(QueueItem item, OperationProgress reported)
    {
        OperationQueueSnapshot? snapshot = null;
        lock (_gate)
        {
            if (item.IsFinalized || !ReferenceEquals(_current, item))
            {
                return;
            }

            var previous = item.Progress;
            var state = NormalizeProgressState(item.Operation.Kind, item.Progress.State, reported.State);
            double? percent = reported.Percent is null
                ? null
                : Math.Clamp(reported.Percent.Value, 0d, 100d);
            var progress = reported with
            {
                OperationId = item.Operation.Id,
                State = state,
                Percent = percent,
                Timestamp = reported.Timestamp == default ? _timeProvider.GetUtcNow() : reported.Timestamp,
                CancellationSupported = reported.CancellationSupported
                    && !item.Operation.RunAsAdministrator
                    && item.Operation.EffectiveTarget is not MsixTarget
            };
            item.Progress = progress;

            // Native WinGet callbacks can arrive much faster than the UI can render them.
            // Keep the latest snapshot exact, publish whole-percent changes immediately,
            // and limit smaller steady-state changes to 10 Hz.
            var timestamp = _timeProvider.GetTimestamp();
            if (ShouldPublishProgress(item, previous, progress, timestamp))
            {
                item.HasPublishedProgress = true;
                item.LastProgressNotificationTimestamp = timestamp;
                snapshot = CreateSnapshotLocked();
            }
        }

        if (snapshot is not null)
        {
            RaiseChanged(snapshot);
        }
    }

    private Task<OperationResult> ExecuteMsixOperationAsync(
        QueueItem item,
        IProgress<OperationProgress> progress)
    {
        if (item.Operation.Kind != PackageOperationKind.Uninstall)
        {
            throw new InvalidOperationException("MSIX targets currently support uninstall only.");
        }

        if (_msixClient is null)
        {
            throw new InvalidOperationException("MSIX package management is unavailable.");
        }

        // RemovePackageAsync cannot be cancelled once deployment begins. Do not pass the
        // queue token through or imply that Windows can safely roll the operation back.
        return _msixClient.UninstallAsync(item.Operation, progress, CancellationToken.None);
    }

    private Task<OperationResult> ExecutePrivilegedOperationAsync(
        QueueItem item,
        IProgress<OperationProgress> progress)
    {
        if (item.Operation.Target is not WingetTarget wingetTarget
            || wingetTarget.Package.IsEmpty
            || string.IsNullOrWhiteSpace(wingetTarget.Package.SourceId)
            || item.Operation.Package != wingetTarget.Package)
        {
            throw new InvalidOperationException(
                "Administrator execution requires an exact WinGet package and source target.");
        }

        if (_privilegedPackageOperationBroker is null
            || !_privilegedPackageOperationBroker.IsAvailable)
        {
            throw new InvalidOperationException(
                "Administrator package execution is unavailable.");
        }

        // Once the queue exposes the UAC preparation state this work is deliberately
        // non-cancelable. The elevated broker owns the exact one-shot operation result.
        return _privilegedPackageOperationBroker.ExecuteElevatedAsync(
            item.Operation,
            progress,
            CancellationToken.None);
    }

    private bool ShouldPublishProgress(
        QueueItem item,
        OperationProgress previous,
        OperationProgress current,
        long timestamp) =>
        !item.HasPublishedProgress
        || previous.State != current.State
        || previous.Percent.HasValue != current.Percent.HasValue
        || ProgressPercentBucket(previous.Percent) != ProgressPercentBucket(current.Percent)
        || !string.Equals(previous.Message, current.Message, StringComparison.Ordinal)
        || _timeProvider.GetElapsedTime(item.LastProgressNotificationTimestamp, timestamp)
            >= ProgressNotificationInterval;

    private static int? ProgressPercentBucket(double? percent) =>
        percent is null ? null : (int)Math.Floor(percent.Value);

    private static WingetError CreateNoChangeDetectedError() => new()
    {
        Kind = WingetErrorKind.NoChangeDetected,
        Code = "InstalledVersionUnchanged",
        Message =
            "WinGet reported completion, but repeated checks still found the previous installed version. Close the app completely, then retry."
    };

    private async Task<bool> PersistHistorySafeAsync(bool publishFailure = true)
    {
        if (_historyStore is null)
        {
            return true;
        }

        OperationResult[] history;
        lock (_gate)
        {
            history = _history.ToArray();
        }

        try
        {
            await _historyStore.SaveAsync(history).ConfigureAwait(false);
            lock (_gate)
            {
                _lastPersistenceError = null;
            }
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OperationQueueSnapshot snapshot;
            lock (_gate)
            {
                _lastPersistenceError = exception;
                snapshot = CreateSnapshotLocked();
            }

            if (publishFailure)
            {
                RaiseChanged(snapshot);
            }
            return false;
        }
    }

    private Task ScheduleHistoryPersistence()
    {
        lock (_persistenceScheduleGate)
        {
            var previous = _persistenceTail;
            _persistenceTail = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await Initialization.ConfigureAwait(false);
                await PersistHistorySafeAsync().ConfigureAwait(false);
            });
            return _persistenceTail;
        }
    }

    private void CompleteIdleIfAppropriate(bool force = false)
    {
        lock (_gate)
        {
            if (force || (_pending.Count == 0 && _current is null))
            {
                _idle.TrySetResult();
            }
        }
    }

    private void AddHistoryLocked(OperationResult result)
    {
        _history.RemoveAll(item => item.OperationId == result.OperationId);
        _history.Insert(0, result);
        TrimHistoryLocked();
    }

    private static bool IsEquivalentActiveOperation(
        PackageOperation existing,
        PackageOperation candidate) =>
        HaveSameTarget(existing.EffectiveTarget, candidate.EffectiveTarget);

    private static bool HaveSameTarget(OperationTarget? left, OperationTarget? right) =>
        (left, right) switch
        {
            (WingetTarget leftWinget, WingetTarget rightWinget) =>
                leftWinget.Package == rightWinget.Package,
            (MsixTarget leftMsix, MsixTarget rightMsix) =>
                string.Equals(
                    leftMsix.PackageFullName,
                    rightMsix.PackageFullName,
                    StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private void TrimHistoryLocked()
    {
        if (_history.Count > MaximumHistory)
        {
            _history.RemoveRange(MaximumHistory, _history.Count - MaximumHistory);
        }
    }

    private OperationQueueSnapshot CreateSnapshotLocked() => new()
    {
        Pending = _pending
            .Where(item => !item.IsFinalized)
            .Select(item => new OperationQueueEntry(item.Operation, item.Progress))
            .ToArray(),
        Current = _current is null ? null : new OperationQueueEntry(_current.Operation, _current.Progress),
        History = _history.ToArray(),
        LastPersistenceError = _lastPersistenceError
    };

    private OperationProgress NewProgress(
        PackageOperation operation,
        PackageOperationState state,
        string? message = null) =>
        new()
        {
            OperationId = operation.Id,
            State = state,
            Message = message,
            Timestamp = _timeProvider.GetUtcNow(),
            CancellationSupported = !operation.RunAsAdministrator
                && operation.EffectiveTarget is not MsixTarget
        };

    private OperationResult CreateCancelledResult(
        PackageOperation operation,
        DateTimeOffset completedAt,
        DateTimeOffset? startedAt = null) =>
        new()
        {
            OperationId = operation.Id,
            Package = operation.Package,
            Target = operation.EffectiveTarget,
            Kind = operation.Kind,
            State = PackageOperationState.Cancelled,
            StartedAt = startedAt ?? completedAt,
            CompletedAt = completedAt,
            AdministratorRetryRequested = operation.RunAsAdministrator,
            Error = new WingetError
            {
                Kind = WingetErrorKind.Cancelled,
                Code = "Cancelled",
                Message = "The operation was cancelled."
            }
        };

    private OperationResult CreateFailedResult(
        PackageOperation operation,
        WingetError error,
        DateTimeOffset startedAt) =>
        new()
        {
            OperationId = operation.Id,
            Package = operation.Package,
            Target = operation.EffectiveTarget,
            Kind = operation.Kind,
            State = PackageOperationState.Failed,
            StartedAt = startedAt,
            CompletedAt = _timeProvider.GetUtcNow(),
            AdministratorRetryRequested = operation.RunAsAdministrator,
            Error = error
        };

    private static OperationResult NormalizeResult(
        PackageOperation operation,
        OperationResult result,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var state = result.State;
        if (result.RebootRequired && state == PackageOperationState.Completed)
        {
            state = PackageOperationState.RebootRequired;
        }

        if (state is not (PackageOperationState.Completed
            or PackageOperationState.Failed
            or PackageOperationState.Cancelled
            or PackageOperationState.RebootRequired))
        {
            state = PackageOperationState.Failed;
            result = result with
            {
                Error = new WingetError
                {
                    Kind = WingetErrorKind.Unknown,
                    Code = "InvalidResult",
                    Message = "WinGet returned a non-terminal operation result."
                }
            };
        }

        return result with
        {
            OperationId = operation.Id,
            DisplayName = operation.DisplayName,
            Package = operation.Package,
            Target = operation.EffectiveTarget,
            Kind = operation.Kind,
            State = state,
            StartedAt = result.StartedAt == default ? startedAt : result.StartedAt,
            CompletedAt = result.CompletedAt == default ? completedAt : result.CompletedAt,
            RebootRequired = result.RebootRequired || state == PackageOperationState.RebootRequired,
            AdministratorRetryRequested = operation.RunAsAdministrator,
            RanAsAdministrator = operation.RunAsAdministrator && result.RanAsAdministrator
        };
    }

    private static WingetError CreateError(Exception exception) => new()
    {
        Kind = exception is COMException ? WingetErrorKind.ComFailure : WingetErrorKind.Unknown,
        Code = exception is COMException comException
            ? $"0x{comException.HResult:X8}"
            : exception.GetType().Name,
        Message = string.IsNullOrWhiteSpace(exception.Message)
            ? "The package operation failed."
            : exception.Message,
        HResult = exception.HResult
    };

    private static PackageOperationState NormalizeProgressState(
        PackageOperationKind kind,
        PackageOperationState current,
        PackageOperationState reported)
    {
        if (reported is PackageOperationState.Completed
            or PackageOperationState.Failed
            or PackageOperationState.Cancelled
            or PackageOperationState.RebootRequired
            or PackageOperationState.Queued)
        {
            return current;
        }

        var expectedExecutingState = kind switch
        {
            PackageOperationKind.Install => PackageOperationState.Installing,
            PackageOperationKind.Upgrade => PackageOperationState.Upgrading,
            PackageOperationKind.Uninstall => PackageOperationState.Uninstalling,
            _ => reported
        };

        if (reported is PackageOperationState.Installing
            or PackageOperationState.Upgrading
            or PackageOperationState.Uninstalling)
        {
            reported = expectedExecutingState;
        }

        return ProgressRank(reported) < ProgressRank(current) ? current : reported;
    }

    private static int ProgressRank(PackageOperationState state) => state switch
    {
        PackageOperationState.Queued => 0,
        PackageOperationState.Resolving => 1,
        PackageOperationState.Downloading => 2,
        PackageOperationState.Installing
            or PackageOperationState.Upgrading
            or PackageOperationState.Uninstalling => 3,
        _ => 4
    };

    private void RaiseChanged(OperationQueueSnapshot snapshot)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var args = new OperationQueueChangedEventArgs(snapshot);
        foreach (EventHandler<OperationQueueChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Observers must not be able to terminate the package operation worker.
            }
        }
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSource()
    {
        var source = NewSource();
        source.SetResult();
        return source;
    }

    private sealed class QueueItem(PackageOperation operation, CancellationTokenSource cancellation) : IDisposable
    {
        public PackageOperation Operation { get; } = operation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public OperationProgress Progress { get; set; } = new()
        {
            OperationId = operation.Id,
            State = PackageOperationState.Queued,
            Timestamp = operation.EnqueuedAt,
            CancellationSupported = operation.EffectiveTarget is not MsixTarget
        };
        public DateTimeOffset StartedAt { get; set; }
        public long LastProgressNotificationTimestamp { get; set; }
        public bool HasPublishedProgress { get; set; }
        public bool IsFinalized { get; set; }
        public CancellationTokenRegistration ExternalCancellationRegistration { get; set; }

        public void Dispose()
        {
            ExternalCancellationRegistration.Dispose();
            Cancellation.Dispose();
        }

        public void RequestCancellation()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A concurrently completed queue item no longer needs cancellation.
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record CancellationRegistrationState(OperationQueue Queue, Guid OperationId);
}
