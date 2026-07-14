using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class OperationQueueTests
{
    [Fact]
    public async Task Queue_ExecutesOperationsInOrderAndNeverOverlapsThem()
    {
        var client = new FakeWingetClient();
        await using var queue = new OperationQueue(client);
        var operations = Enumerable.Range(1, 4)
            .Select(index => Operation($"Package.{index}"))
            .ToArray();

        foreach (var operation in operations)
        {
            queue.Enqueue(operation);
        }

        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(operations.Select(item => item.Id), client.ExecutionOrder);
        Assert.Equal(1, client.MaximumConcurrency);
        Assert.Equal(operations.Reverse().Select(item => item.Id), queue.Snapshot.History.Select(item => item.OperationId));
    }

    [Fact]
    public async Task TryCancel_RemovesQueuedOperationWithoutCallingWinget()
    {
        var firstStarted = NewSource();
        var releaseFirst = NewSource();
        var client = new FakeWingetClient
        {
            Execute = async (operation, progress, cancellationToken) =>
            {
                progress?.Report(Progress(operation, PackageOperationState.Installing));
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return Success(operation);
            }
        };
        await using var queue = new OperationQueue(client);
        var first = Operation("Package.First");
        var second = Operation("Package.Second");

        queue.Enqueue(first);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Enqueue(second);

        Assert.True(queue.TryCancel(second.Id));
        Assert.Contains(queue.Snapshot.History, item =>
            item.OperationId == second.Id && item.State == PackageOperationState.Cancelled);

        releaseFirst.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([first.Id], client.ExecutionOrder);
    }

    [Fact]
    public async Task TryCancel_IsRejectedAfterInstallerTakesControl()
    {
        var installing = NewSource();
        var release = NewSource();
        var client = new FakeWingetClient
        {
            Execute = async (operation, progress, cancellationToken) =>
            {
                progress?.Report(Progress(operation, PackageOperationState.Downloading, 20));
                progress?.Report(Progress(operation, PackageOperationState.Installing, 40));
                installing.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Success(operation);
            }
        };
        await using var queue = new OperationQueue(client);
        using var externalCancellation = new CancellationTokenSource();
        var operation = Operation("Package.Boundary");
        var states = new ConcurrentQueue<PackageOperationState>();
        queue.Changed += (_, args) =>
        {
            var entry = args.Snapshot.Current;
            if (entry?.Operation.Id == operation.Id)
            {
                states.Enqueue(entry.Progress.State);
            }
            else if (args.Snapshot.History.FirstOrDefault()?.OperationId == operation.Id)
            {
                states.Enqueue(args.Snapshot.History[0].State);
            }
        };

        queue.Enqueue(operation, externalCancellation.Token);
        await installing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(queue.TryCancel(operation.Id));
        externalCancellation.Cancel();
        release.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        AssertOrderedSubset(
            states,
            PackageOperationState.Resolving,
            PackageOperationState.Downloading,
            PackageOperationState.Installing,
            PackageOperationState.Completed);
    }

    [Fact]
    public async Task TryCancel_DuringDownloadCancelsActiveOperation()
    {
        var downloading = NewSource();
        var client = new FakeWingetClient
        {
            Execute = async (operation, progress, cancellationToken) =>
            {
                progress?.Report(Progress(operation, PackageOperationState.Downloading));
                downloading.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Success(operation);
            }
        };
        await using var queue = new OperationQueue(client);
        var operation = Operation("Package.Download");

        queue.Enqueue(operation);
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(queue.TryCancel(operation.Id));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var result = Assert.Single(queue.Snapshot.History);
        Assert.Equal(PackageOperationState.Cancelled, result.State);
        Assert.Equal(WingetErrorKind.Cancelled, result.Error?.Kind);
    }

    [Fact]
    public async Task ClientFailure_IsTypedAndDoesNotStopFollowingOperations()
    {
        var call = 0;
        var client = new FakeWingetClient
        {
            Execute = (operation, _, _) =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    throw new WingetException(new WingetError
                    {
                        Kind = WingetErrorKind.PolicyBlocked,
                        Code = "PolicyBlocked",
                        Message = "Installation is disabled by policy."
                    });
                }

                return Task.FromResult(Success(operation));
            }
        };
        await using var queue = new OperationQueue(client);

        queue.Enqueue(Operation("Package.Blocked"));
        queue.Enqueue(Operation("Package.Allowed"));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, client.ExecutionOrder.Count);
        Assert.Equal(PackageOperationState.Completed, queue.Snapshot.History[0].State);
        Assert.Equal(WingetErrorKind.PolicyBlocked, queue.Snapshot.History[1].Error?.Kind);
    }

    [Fact]
    public async Task Queue_DispatchesEachOperationToItsMatchingClientMethod()
    {
        var client = new FakeWingetClient();
        await using var queue = new OperationQueue(client);

        queue.Enqueue(Operation("Package.Install", PackageOperationKind.Install));
        queue.Enqueue(Operation("Package.Upgrade", PackageOperationKind.Upgrade));
        queue.Enqueue(Operation("Package.Uninstall", PackageOperationKind.Uninstall));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            [PackageOperationKind.Install, PackageOperationKind.Upgrade, PackageOperationKind.Uninstall],
            client.InvokedMethods);
    }

    [Fact]
    public async Task NonTerminalClientResult_IsConvertedToTypedFailure()
    {
        var client = new FakeWingetClient
        {
            Execute = (operation, _, _) => Task.FromResult(Success(operation) with
            {
                State = PackageOperationState.Downloading
            })
        };
        await using var queue = new OperationQueue(client);

        queue.Enqueue(Operation("Package.InvalidResult"));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var result = Assert.Single(queue.Snapshot.History);
        Assert.Equal(PackageOperationState.Failed, result.State);
        Assert.Equal("InvalidResult", result.Error?.Code);
    }

    [Fact]
    public async Task RebootFlag_IsNormalizedToRebootRequiredState()
    {
        var client = new FakeWingetClient
        {
            Execute = (operation, _, _) => Task.FromResult(Success(operation) with
            {
                RebootRequired = true
            })
        };
        await using var queue = new OperationQueue(client);

        queue.Enqueue(Operation("Package.Reboot"));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var result = Assert.Single(queue.Snapshot.History);
        Assert.Equal(PackageOperationState.RebootRequired, result.State);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ComException_IsTranslatedWithoutStoppingQueue()
    {
        var call = 0;
        var client = new FakeWingetClient
        {
            Execute = (operation, _, _) => Interlocked.Increment(ref call) == 1
                ? throw new COMException("COM activation failed.", unchecked((int)0x80004005))
                : Task.FromResult(Success(operation))
        };
        await using var queue = new OperationQueue(client);

        queue.Enqueue(Operation("Package.ComFailure"));
        queue.Enqueue(Operation("Package.AfterFailure"));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PackageOperationState.Completed, queue.Snapshot.History[0].State);
        Assert.Equal(WingetErrorKind.ComFailure, queue.Snapshot.History[1].Error?.Kind);
        Assert.Equal("0x80004005", queue.Snapshot.History[1].Error?.Code);
    }

    [Fact]
    public async Task History_RetainsOnlyMostRecentOneHundredResults()
    {
        var client = new FakeWingetClient();
        await using var queue = new OperationQueue(client);
        var operations = Enumerable.Range(0, 105)
            .Select(index => Operation($"Package.{index}"))
            .ToArray();

        foreach (var operation in operations)
        {
            queue.Enqueue(operation);
        }

        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(100, queue.Snapshot.History.Count);
        Assert.Equal(operations[^1].Id, queue.Snapshot.History[0].OperationId);
        Assert.Equal(operations[5].Id, queue.Snapshot.History[^1].OperationId);
    }

    [Fact]
    public async Task History_IsPersistedAndLoadedByANewQueue()
    {
        var store = new InMemoryHistoryStore();
        var operation = Operation("Package.Persistent");

        await using (var first = new OperationQueue(new FakeWingetClient(), store))
        {
            first.Enqueue(operation);
            await first.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        await using var second = new OperationQueue(new FakeWingetClient(), store);
        await second.Initialization;

        var loaded = Assert.Single(second.Snapshot.History);
        Assert.Equal(operation.Id, loaded.OperationId);
        Assert.True(store.SaveCount > 0);
    }

    [Fact]
    public async Task ClearHistory_RaisesChangedAndPersistsAnEmptyHistory()
    {
        var store = new InMemoryHistoryStore();
        var queue = new OperationQueue(new FakeWingetClient(), store);
        var operation = Operation("Package.Clearable");
        var observedEmptyHistory = false;
        queue.Changed += (_, args) =>
        {
            if (args.Snapshot.History.Count == 0)
            {
                observedEmptyHistory = true;
            }
        };

        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(queue.Snapshot.History);

        queue.ClearHistory();

        Assert.Empty(queue.Snapshot.History);
        Assert.True(observedEmptyHistory);
        await queue.DisposeAsync();

        await using var reloaded = new OperationQueue(new FakeWingetClient(), store);
        await reloaded.Initialization;
        Assert.Empty(reloaded.Snapshot.History);
    }

    [Fact]
    public async Task PersistenceFailure_IsObservableAndDoesNotFailOperations()
    {
        var store = new FailingHistoryStore(failSave: true);
        var client = new FakeWingetClient();
        await using var queue = new OperationQueue(client, store);

        queue.Enqueue(Operation("Package.One"));
        queue.Enqueue(Operation("Package.Two"));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, queue.Snapshot.History.Count);
        Assert.All(queue.Snapshot.History, result => Assert.True(result.IsSuccess));
        Assert.IsType<IOException>(queue.Snapshot.LastPersistenceError);
        Assert.Equal(2, client.ExecutionOrder.Count);
    }

    [Fact]
    public async Task HistoryLoadFailure_IsObservableAndQueueStillStarts()
    {
        var store = new FailingHistoryStore(failLoad: true);
        var client = new FakeWingetClient();
        await using var queue = new OperationQueue(client, store);

        await queue.Initialization;
        Assert.IsType<InvalidDataException>(queue.Snapshot.LastPersistenceError);

        queue.Enqueue(Operation("Package.AfterLoadFailure"));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(Assert.Single(queue.Snapshot.History).IsSuccess);
        Assert.Null(queue.Snapshot.LastPersistenceError);
    }

    private static PackageOperation Operation(
        string id,
        PackageOperationKind kind = PackageOperationKind.Install) => PackageOperation.Create(
        kind,
        new PackageKey(id, "winget"));

    private static OperationProgress Progress(
        PackageOperation operation,
        PackageOperationState state,
        double? percent = null) =>
        new()
        {
            OperationId = operation.Id,
            State = state,
            Percent = percent
        };

    private static OperationResult Success(PackageOperation operation) => new()
    {
        OperationId = operation.Id,
        Package = operation.Package,
        Kind = operation.Kind,
        State = PackageOperationState.Completed
    };

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertOrderedSubset(
        IEnumerable<PackageOperationState> actual,
        params PackageOperationState[] expected)
    {
        var values = actual.ToArray();
        var searchFrom = 0;
        foreach (var state in expected)
        {
            var index = Array.IndexOf(values, state, searchFrom);
            Assert.True(index >= searchFrom, $"State {state} was not found in order. Actual: {string.Join(", ", values)}");
            searchFrom = index + 1;
        }
    }

    private sealed class InMemoryHistoryStore : IOperationHistoryStore
    {
        private readonly object _gate = new();
        private OperationResult[] _results = [];

        public int SaveCount { get; private set; }

        public Task<IReadOnlyList<OperationResult>> LoadAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<OperationResult>>(_results.ToArray());
            }
        }

        public Task SaveAsync(
            IReadOnlyList<OperationResult> results,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _results = results.ToArray();
                SaveCount++;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingHistoryStore(bool failLoad = false, bool failSave = false) : IOperationHistoryStore
    {
        public Task<IReadOnlyList<OperationResult>> LoadAsync(CancellationToken cancellationToken = default) =>
            failLoad
                ? Task.FromException<IReadOnlyList<OperationResult>>(
                    new InvalidDataException("History could not be loaded."))
                : Task.FromResult<IReadOnlyList<OperationResult>>(Array.Empty<OperationResult>());

        public Task SaveAsync(
            IReadOnlyList<OperationResult> results,
            CancellationToken cancellationToken = default) =>
            failSave
                ? Task.FromException(new IOException("History could not be saved."))
                : Task.CompletedTask;
    }

    private sealed class FakeWingetClient : IWingetClient
    {
        private int _active;
        private int _maximumConcurrency;

        public ConcurrentQueue<Guid> ExecutionOrder { get; } = new();
        public ConcurrentQueue<PackageOperationKind> InvokedMethods { get; } = new();
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public Func<PackageOperation, IProgress<OperationProgress>?, CancellationToken, Task<OperationResult>> Execute { get; init; } =
            static (operation, _, _) => Task.FromResult(Success(operation));

        public Task<WingetCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WingetCapabilities { IsAvailable = true, ContractVersion = 6 });

        public Task<PackageSearchResult> SearchAsync(PackageQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PackageSearchResult());

        public Task<IReadOnlyList<PackageSourceStatus>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSourceStatus>>(Array.Empty<PackageSourceStatus>());

        public Task<IReadOnlyList<PackageSummary>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSummary>>(Array.Empty<PackageSummary>());

        public Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSummary>>(Array.Empty<PackageSummary>());

        public Task<PackageDetails?> GetPackageDetailsAsync(
            PackageKey package,
            InstallPreferences? preferences = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PackageDetails?>(null);

        public Task<OperationResult> InstallAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            RunAsync(PackageOperationKind.Install, operation, progress, cancellationToken);

        public Task<OperationResult> UpgradeAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            RunAsync(PackageOperationKind.Upgrade, operation, progress, cancellationToken);

        public Task<OperationResult> UninstallAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            RunAsync(PackageOperationKind.Uninstall, operation, progress, cancellationToken);

        private async Task<OperationResult> RunAsync(
            PackageOperationKind invokedMethod,
            PackageOperation operation,
            IProgress<OperationProgress>? progress,
            CancellationToken cancellationToken)
        {
            ExecutionOrder.Enqueue(operation.Id);
            InvokedMethods.Enqueue(invokedMethod);
            var active = Interlocked.Increment(ref _active);
            SetMaximum(active);

            try
            {
                await Task.Yield();
                return await Execute(operation, progress, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void SetMaximum(int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _maximumConcurrency);
                if (current >= value)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maximumConcurrency, value, current) != current);
        }
    }
}
