using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class OperationQueueTests
{
    [Fact]
    public async Task TryBeginShutdownIfIdle_ClosesQueueAtomically()
    {
        await using var queue = new OperationQueue(new FakeWingetClient());

        Assert.True(queue.TryBeginShutdownIfIdle());
        Assert.True(queue.TryBeginShutdownIfIdle());
        Assert.Throws<InvalidOperationException>(() =>
            queue.Enqueue(Operation("Package.AfterShutdownGate")));
    }

    [Fact]
    public async Task TryBeginShutdownIfIdle_RefusesWhileBusyAndLeavesQueueUsable()
    {
        var started = NewSource();
        var release = NewSource();
        var client = new FakeWingetClient
        {
            Execute = async (operation, _, cancellationToken) =>
            {
                if (operation.Package.Id == "Package.Active")
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }

                return Success(operation);
            }
        };
        await using var queue = new OperationQueue(client);

        queue.Enqueue(Operation("Package.Active"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(queue.TryBeginShutdownIfIdle());
        queue.Enqueue(Operation("Package.StillAccepted"));

        release.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(queue.TryBeginShutdownIfIdle());
    }

    [Fact]
    public async Task TryBeginShutdownIfIdle_RacingEnqueueHasExactlyOneWinner()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var release = NewSource();
            var client = new FakeWingetClient
            {
                Execute = async (operation, _, cancellationToken) =>
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return Success(operation);
                }
            };
            await using var queue = new OperationQueue(client);
            using var start = new ManualResetEventSlim(false);

            Task<bool> shutdown = Task.Run(() =>
            {
                start.Wait();
                return queue.TryBeginShutdownIfIdle();
            });
            Task<bool> enqueue = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    queue.Enqueue(Operation($"Package.Race.{iteration}"));
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });

            start.Set();
            bool shutdownAccepted = await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            bool enqueueAccepted = await enqueue.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();

            Assert.NotEqual(shutdownAccepted, enqueueAccepted);
            if (enqueueAccepted)
            {
                await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(queue.TryBeginShutdownIfIdle());
            }
        }
    }

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
    public async Task Queue_RejectsAnyMutationForAnActiveOrPendingExactTarget()
    {
        var started = NewSource();
        var release = NewSource();
        var client = new FakeWingetClient
        {
            Execute = async (operation, _, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Success(operation);
            }
        };
        await using var queue = new OperationQueue(client);
        var first = PackageOperation.Create(
            PackageOperationKind.Upgrade,
            new PackageKey("Contoso.App", "winget"));

        queue.Enqueue(first);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duplicate = Assert.Throws<DuplicatePackageOperationException>(() =>
            queue.Enqueue(PackageOperation.Create(
                PackageOperationKind.Upgrade,
                new PackageKey("Contoso.App", "winget"))));
        Assert.Equal(first.Id, duplicate.ExistingOperationId);

        var differentKind = Assert.Throws<DuplicatePackageOperationException>(() =>
            queue.Enqueue(PackageOperation.Create(
                PackageOperationKind.Uninstall,
                new PackageKey("Contoso.App", "winget"))));
        Assert.Equal(first.Id, differentKind.ExistingOperationId);

        var otherSource = PackageOperation.Create(
            PackageOperationKind.Upgrade,
            new PackageKey("Contoso.App", "store"));
        queue.Enqueue(otherSource);

        var pendingDuplicate = Assert.Throws<DuplicatePackageOperationException>(() =>
            queue.Enqueue(PackageOperation.Create(
                PackageOperationKind.Install,
                new PackageKey("Contoso.App", "store"))));
        Assert.Equal(otherSource.Id, pendingDuplicate.ExistingOperationId);

        release.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, client.ExecutionOrder.Count);

        queue.Enqueue(PackageOperation.Create(
            PackageOperationKind.Upgrade,
            new PackageKey("Contoso.App", "winget")));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, client.ExecutionOrder.Count);
    }

    [Fact]
    public async Task Queue_DeduplicatesMsixTargetsCaseInsensitively()
    {
        var started = NewSource();
        var release = NewSource();
        var msix = new FakeMsixClient(async operation =>
        {
            started.TrySetResult();
            await release.Task;
            return Success(operation) with { Target = operation.EffectiveTarget };
        });
        await using var queue = new OperationQueue(new FakeWingetClient(), msixClient: msix);
        var first = MsixRemoval("Contoso.App_1.0.0.0_x64__Publisher");

        queue.Enqueue(first);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var duplicate = Assert.Throws<DuplicatePackageOperationException>(() =>
            queue.Enqueue(MsixRemoval("contoso.app_1.0.0.0_x64__publisher")));
        Assert.Equal(first.Id, duplicate.ExistingOperationId);

        release.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, msix.CallCount);
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
    public async Task ProgressBurst_CoalescesNotificationsAndKeepsLatestSnapshot()
    {
        var burstReported = NewSource();
        var release = NewSource();
        var client = new FakeWingetClient
        {
            Execute = async (operation, progress, cancellationToken) =>
            {
                for (var index = 0; index < 10_000; index++)
                {
                    progress?.Report(new OperationProgress
                    {
                        OperationId = operation.Id,
                        State = PackageOperationState.Downloading,
                        Percent = index / 100d,
                        Message = "Downloading",
                        BytesTransferred = index,
                        BytesTotal = 10_000,
                        Timestamp = default
                    });
                }

                burstReported.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Success(operation);
            }
        };
        await using var queue = new OperationQueue(client, timeProvider: new ManualTimeProvider());
        var operation = Operation("Package.ProgressBurst");
        var publishedProgress = new ConcurrentQueue<OperationProgress>();
        queue.Changed += (_, args) =>
        {
            if (args.Snapshot.Current is { } current
                && current.Operation.Id == operation.Id
                && current.Progress.State == PackageOperationState.Downloading)
            {
                publishedProgress.Enqueue(current.Progress);
            }
        };

        queue.Enqueue(operation);
        await burstReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activeSnapshot = queue.Snapshot.Current;
        var published = publishedProgress.ToArray();

        release.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(activeSnapshot);
        Assert.Equal(99.99d, activeSnapshot.Progress.Percent);
        Assert.Equal(9_999, activeSnapshot.Progress.BytesTransferred);
        Assert.Equal(100, published.Length);
        Assert.Equal(99d, published[^1].Percent);
        Assert.Equal(9_900, published[^1].BytesTransferred);
        var publishedLag = activeSnapshot.Progress.Percent.GetValueOrDefault()
            - published[^1].Percent.GetValueOrDefault();
        Assert.True(publishedLag is >= 0d and < 1d, $"Published progress lagged by {publishedLag} percentage points.");
        Assert.True(Assert.Single(queue.Snapshot.History).IsSuccess);
    }

    [Fact]
    public async Task ProgressWithinPercentBucket_PublishesAtTenHertz()
    {
        var reportsFinished = NewSource();
        var release = NewSource();
        var timeProvider = new ManualTimeProvider();
        var client = new FakeWingetClient
        {
            Execute = async (operation, progress, cancellationToken) =>
            {
                progress?.Report(Progress(operation, PackageOperationState.Downloading, 10d));
                progress?.Report(Progress(operation, PackageOperationState.Downloading, 10.1d));
                timeProvider.Advance(TimeSpan.FromMilliseconds(99));
                progress?.Report(Progress(operation, PackageOperationState.Downloading, 10.2d));
                timeProvider.Advance(TimeSpan.FromMilliseconds(1));
                progress?.Report(Progress(operation, PackageOperationState.Downloading, 10.3d));
                reportsFinished.TrySetResult();

                await release.Task.WaitAsync(cancellationToken);
                return Success(operation);
            }
        };
        await using var queue = new OperationQueue(client, timeProvider: timeProvider);
        var operation = Operation("Package.ProgressInterval");
        var publishedProgress = new ConcurrentQueue<double?>();
        queue.Changed += (_, args) =>
        {
            if (args.Snapshot.Current is { } current
                && current.Operation.Id == operation.Id
                && current.Progress.State == PackageOperationState.Downloading)
            {
                publishedProgress.Enqueue(current.Progress.Percent);
            }
        };

        queue.Enqueue(operation);
        await reportsFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var published = publishedProgress.ToArray();

        release.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([10d, 10.3d], published);
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
    public async Task ElevatedWingetOperation_UsesOnlyBrokerAndNormalizesExactResultIdentity()
    {
        var client = new FakeWingetClient();
        var broker = new FakePrivilegedPackageOperationBroker
        {
            Execute = static (operation, _, _) => Task.FromResult(new OperationResult
            {
                OperationId = Guid.NewGuid(),
                Package = new PackageKey("Spoofed.Package", "spoofed"),
                Target = new WingetTarget
                {
                    Package = new PackageKey("Spoofed.Package", "spoofed")
                },
                Kind = PackageOperationKind.Uninstall,
                State = PackageOperationState.Completed,
                RanAsAdministrator = true
            })
        };
        await using var queue = new OperationQueue(
            client,
            privilegedPackageOperationBroker: broker);
        var operation = Operation("Package.Elevated", PackageOperationKind.Upgrade) with
        {
            RunAsAdministrator = true
        };

        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(client.ExecutionOrder);
        Assert.Equal(1, broker.CallCount);
        Assert.Equal(operation.Id, Assert.Single(broker.Operations).Id);
        var result = Assert.Single(queue.Snapshot.History);
        Assert.Equal(operation.Id, result.OperationId);
        Assert.Equal(operation.Package, result.Package);
        Assert.Equal(operation.EffectiveTarget, result.Target);
        Assert.Equal(operation.Kind, result.Kind);
        Assert.True(result.AdministratorRetryRequested);
        Assert.True(result.RanAsAdministrator);
    }

    [Fact]
    public async Task ElevatedOperation_WithoutBrokerFailsClosedWithoutCallingWinget()
    {
        var client = new FakeWingetClient();
        await using var queue = new OperationQueue(client);
        var operation = Operation("Package.NoBroker") with
        {
            RunAsAdministrator = true
        };

        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(client.ExecutionOrder);
        var result = Assert.Single(queue.Snapshot.History);
        Assert.Equal(PackageOperationState.Failed, result.State);
        Assert.True(result.AdministratorRetryRequested);
        Assert.False(result.RanAsAdministrator);
        Assert.Contains("Administrator package execution is unavailable", result.Error?.Message);
    }

    [Fact]
    public async Task ElevatedOperation_WithUnavailableBrokerFailsBeforeBrokerInvocation()
    {
        var client = new FakeWingetClient();
        var broker = new FakePrivilegedPackageOperationBroker { IsAvailable = false };
        await using var queue = new OperationQueue(
            client,
            privilegedPackageOperationBroker: broker);
        var operation = Operation("Package.UnavailableBroker") with
        {
            RunAsAdministrator = true
        };

        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(client.ExecutionOrder);
        Assert.Equal(0, broker.CallCount);
        var result = Assert.Single(queue.Snapshot.History);
        Assert.Equal(PackageOperationState.Failed, result.State);
        Assert.False(result.RanAsAdministrator);
    }

    [Fact]
    public async Task ElevatedOperation_IsNonCancelableAsSoonAsUacPreparationStarts()
    {
        var brokerStarted = NewSource();
        var releaseBroker = NewSource();
        var broker = new FakePrivilegedPackageOperationBroker
        {
            Execute = async (operation, progress, _) =>
            {
                progress?.Report(new OperationProgress
                {
                    OperationId = operation.Id,
                    State = PackageOperationState.Installing,
                    Message = "Elevated helper running",
                    CancellationSupported = true
                });
                brokerStarted.TrySetResult();
                await releaseBroker.Task;
                return Success(operation) with { RanAsAdministrator = true };
            }
        };
        await using var queue = new OperationQueue(
            new FakeWingetClient(),
            privilegedPackageOperationBroker: broker);
        var operation = Operation("Package.NonCancelable") with
        {
            RunAsAdministrator = true
        };
        var currentProgress = new ConcurrentQueue<OperationProgress>();
        queue.Changed += (_, args) =>
        {
            if (args.Snapshot.Current is { } current
                && current.Operation.Id == operation.Id)
            {
                currentProgress.Enqueue(current.Progress);
            }
        };

        queue.Enqueue(operation);
        await brokerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(currentProgress, item =>
            item.Message == "Preparing administrator approval (UAC)..."
            && !item.CanCancel);
        Assert.False(queue.Snapshot.Current!.Progress.CanCancel);
        Assert.False(queue.TryCancel(operation.Id));
        Assert.False(broker.LastCancellationToken.CanBeCanceled);

        releaseBroker.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(queue.Snapshot.History).RanAsAdministrator);
    }

    [Fact]
    public async Task ElevatedBrokerResult_DoesNotInventAdministratorExecution()
    {
        var broker = new FakePrivilegedPackageOperationBroker
        {
            Execute = static (operation, _, _) => Task.FromResult(Success(operation) with
            {
                RanAsAdministrator = false
            })
        };
        await using var queue = new OperationQueue(
            new FakeWingetClient(),
            privilegedPackageOperationBroker: broker);
        var operation = Operation("Package.NoElevationProof") with
        {
            RunAsAdministrator = true
        };

        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var result = Assert.Single(queue.Snapshot.History);
        Assert.True(result.IsSuccess);
        Assert.False(result.RanAsAdministrator);
    }

    [Fact]
    public async Task QueuedElevatedOperation_RemainsCancelableBeforeUacPreparation()
    {
        var firstStarted = NewSource();
        var releaseFirst = NewSource();
        var client = new FakeWingetClient
        {
            Execute = async (operation, _, cancellationToken) =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return Success(operation);
            }
        };
        var broker = new FakePrivilegedPackageOperationBroker();
        await using var queue = new OperationQueue(
            client,
            privilegedPackageOperationBroker: broker);
        queue.Enqueue(Operation("Package.First"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var elevated = Operation("Package.QueuedElevated") with
        {
            RunAsAdministrator = true
        };

        queue.Enqueue(elevated);

        var pending = Assert.Single(queue.Snapshot.Pending);
        Assert.True(pending.Progress.CanCancel);
        Assert.True(queue.TryCancel(elevated.Id));

        releaseFirst.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, broker.CallCount);
        Assert.Contains(queue.Snapshot.History, result =>
            result.OperationId == elevated.Id
            && result.State == PackageOperationState.Cancelled
            && result.AdministratorRetryRequested
            && !result.RanAsAdministrator);
    }

    [Fact]
    public async Task ElevatedMsixOperation_IsRejectedWithoutCallingEitherMutationClient()
    {
        var client = new FakeWingetClient();
        var msix = new FakeMsixClient(operation => Task.FromResult(Success(operation)));
        var broker = new FakePrivilegedPackageOperationBroker();
        await using var queue = new OperationQueue(
            client,
            msixClient: msix,
            privilegedPackageOperationBroker: broker);
        var operation = MsixRemoval("Contoso.App_1.0.0.0_x64__publisher") with
        {
            RunAsAdministrator = true
        };

        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(client.ExecutionOrder);
        Assert.Equal(0, msix.CallCount);
        Assert.Equal(0, broker.CallCount);
        var result = Assert.Single(queue.Snapshot.History);
        Assert.Equal(PackageOperationState.Failed, result.State);
        Assert.Contains("exact WinGet package and source target", result.Error?.Message);
    }

    [Fact]
    public async Task ElevatedOperation_RejectsLegacyImplicitWingetTarget()
    {
        var client = new FakeWingetClient();
        var broker = new FakePrivilegedPackageOperationBroker();
        await using var queue = new OperationQueue(
            client,
            privilegedPackageOperationBroker: broker);
        var operation = new PackageOperation
        {
            Package = new PackageKey("Package.Implicit", "winget"),
            Target = null,
            Kind = PackageOperationKind.Install,
            DisplayName = "Implicit target",
            RunAsAdministrator = true
        };

        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(client.ExecutionOrder);
        Assert.Equal(0, broker.CallCount);
        var result = Assert.Single(queue.Snapshot.History);
        Assert.Equal(PackageOperationState.Failed, result.State);
        Assert.Contains("exact WinGet package and source target", result.Error?.Message);
    }

    [Fact]
    public async Task NormalWingetOperation_DoesNotUseAvailableElevatedBroker()
    {
        var client = new FakeWingetClient();
        var broker = new FakePrivilegedPackageOperationBroker();
        await using var queue = new OperationQueue(
            client,
            privilegedPackageOperationBroker: broker);
        var operation = Operation("Package.Normal");

        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([operation.Id], client.ExecutionOrder);
        Assert.Equal(0, broker.CallCount);
        var result = Assert.Single(queue.Snapshot.History);
        Assert.False(result.AdministratorRetryRequested);
        Assert.False(result.RanAsAdministrator);
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
    public async Task NoChangeReclassification_IsDurableAndPreservesDiagnostics()
    {
        var store = new InMemoryHistoryStore();
        var operation = Operation("Package.NoChange", PackageOperationKind.Upgrade);
        var diagnostic = new OperationDiagnosticReference
        {
            Provider = OperationDiagnosticProvider.Winget,
            ReferenceId = operation.Id
        };
        var client = new FakeWingetClient
        {
            Execute = (candidate, _, _) => Task.FromResult(Success(candidate) with
            {
                Diagnostic = diagnostic
            })
        };

        await using (var queue = new OperationQueue(client, store))
        {
            queue.Enqueue(operation);
            await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(await queue.TryMarkUpgradeNoChangeDetectedAsync(
                operation.Id,
                operation.Package));
            var result = Assert.Single(queue.Snapshot.History);
            Assert.Equal(PackageOperationState.Failed, result.State);
            Assert.Equal(WingetErrorKind.NoChangeDetected, result.Error?.Kind);
            Assert.Equal("InstalledVersionUnchanged", result.Error?.Code);
            Assert.Equal(diagnostic, result.Diagnostic);
        }

        await using var reloaded = new OperationQueue(new FakeWingetClient(), store);
        await reloaded.Initialization;
        var persisted = Assert.Single(reloaded.Snapshot.History);
        Assert.Equal(PackageOperationState.Failed, persisted.State);
        Assert.Equal(WingetErrorKind.NoChangeDetected, persisted.Error?.Kind);
        Assert.Equal(diagnostic, persisted.Diagnostic);
    }

    [Fact]
    public async Task NoChangeReclassification_ConflictingHistoryIdentityReturnsFalse()
    {
        var store = new InMemoryHistoryStore();
        await using var queue = new OperationQueue(new FakeWingetClient(), store);
        var operation = Operation("Package.Other", PackageOperationKind.Upgrade);
        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(await queue.TryMarkUpgradeNoChangeDetectedAsync(
            operation.Id,
            new PackageKey("Package.Other", "private")));
        Assert.True(Assert.Single(queue.Snapshot.History).IsSuccess);
    }

    [Fact]
    public async Task NoChangeReclassification_ReconstructsMissingHistoryDurably()
    {
        var store = new InMemoryHistoryStore();
        var operationId = Guid.NewGuid();
        var package = new PackageKey("Package.Trimmed", "winget");

        await using (var queue = new OperationQueue(new FakeWingetClient(), store))
        {
            await queue.Initialization;
            Assert.True(await queue.TryMarkUpgradeNoChangeDetectedAsync(operationId, package));

            var result = Assert.Single(queue.Snapshot.History);
            Assert.Equal(operationId, result.OperationId);
            Assert.Equal(package, result.Package);
            Assert.Equal(PackageOperationKind.Upgrade, result.Kind);
            Assert.Equal(PackageOperationState.Failed, result.State);
            Assert.Equal(WingetErrorKind.NoChangeDetected, result.Error?.Kind);
        }

        await using var reloaded = new OperationQueue(new FakeWingetClient(), store);
        await reloaded.Initialization;
        Assert.Equal(
            WingetErrorKind.NoChangeDetected,
            Assert.Single(reloaded.Snapshot.History).Error?.Kind);
    }

    [Fact]
    public async Task NoChangeReclassification_MissingHistoryPersistenceFailureRestoresEmptyHistory()
    {
        var store = new FailingHistoryStore(failSave: true);
        await using var queue = new OperationQueue(new FakeWingetClient(), store);
        await queue.Initialization;

        Assert.False(await queue.TryMarkUpgradeNoChangeDetectedAsync(
            Guid.NewGuid(),
            new PackageKey("Package.Trimmed", "winget")));
        Assert.Empty(queue.Snapshot.History);
        Assert.IsType<IOException>(queue.Snapshot.LastPersistenceError);
    }

    [Fact]
    public async Task NoChangeReclassification_PersistenceFailureRollsBackInMemory()
    {
        var store = new FailingHistoryStore(failSave: true);
        await using var queue = new OperationQueue(new FakeWingetClient(), store);
        var operation = Operation("Package.PersistenceFailure", PackageOperationKind.Upgrade);
        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(await queue.TryMarkUpgradeNoChangeDetectedAsync(
            operation.Id,
            operation.Package));

        var result = Assert.Single(queue.Snapshot.History);
        Assert.Equal(PackageOperationState.Completed, result.State);
        Assert.Null(result.Error);
        Assert.IsType<IOException>(queue.Snapshot.LastPersistenceError);
    }

    [Fact]
    public async Task NoChangeReclassification_ConcurrentClearDoesNotReportCommit()
    {
        var store = new BlockingHistoryStore();
        await using var queue = new OperationQueue(new FakeWingetClient(), store);
        var operation = Operation("Package.Cleared", PackageOperationKind.Upgrade);
        queue.Enqueue(operation);
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        store.BlockNextSave();

        var reclassification = queue.TryMarkUpgradeNoChangeDetectedAsync(
            operation.Id,
            operation.Package);
        await store.SaveStarted.WaitAsync(TimeSpan.FromSeconds(5));
        queue.ClearHistory();
        store.ReleaseSave();

        Assert.False(await reclassification.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(queue.Snapshot.History);
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

    [Fact]
    public async Task MsixRemoval_IsDispatchedWithoutOfferingCancellation()
    {
        var started = NewSource();
        var release = NewSource();
        var msix = new FakeMsixClient(async operation =>
        {
            started.TrySetResult();
            await release.Task;
            return Success(operation) with { Target = operation.EffectiveTarget };
        });
        await using var queue = new OperationQueue(new FakeWingetClient(), msixClient: msix);
        var operation = new PackageOperation
        {
            Kind = PackageOperationKind.Uninstall,
            DisplayName = "Contoso App",
            Target = new MsixTarget
            {
                PackageFullName = "Contoso.App_1.0.0.0_x64__publisher",
                PackageFamilyName = "Contoso.App_publisher"
            }
        };

        queue.Enqueue(operation);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(queue.Snapshot.Current!.Progress.CanCancel);
        Assert.False(queue.TryCancel(operation.Id));

        release.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<MsixTarget>(Assert.Single(queue.Snapshot.History).Target);
        Assert.Equal(1, msix.CallCount);
    }

    private static PackageOperation Operation(
        string id,
        PackageOperationKind kind = PackageOperationKind.Install) => PackageOperation.Create(
        kind,
        new PackageKey(id, "winget"));

    private static PackageOperation MsixRemoval(string packageFullName) => new()
    {
        Kind = PackageOperationKind.Uninstall,
        DisplayName = "Contoso App",
        Target = new MsixTarget
        {
            PackageFullName = packageFullName,
            PackageFamilyName = "Contoso.App_publisher"
        }
    };

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

    private sealed class BlockingHistoryStore : IOperationHistoryStore
    {
        private readonly object _gate = new();
        private OperationResult[] _results = [];
        private TaskCompletionSource _saveStarted = NewSource();
        private TaskCompletionSource _releaseSave = NewSource();
        private bool _blockNextSave;

        public Task SaveStarted => _saveStarted.Task;

        public Task<IReadOnlyList<OperationResult>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<OperationResult>>(_results.ToArray());
            }
        }

        public async Task SaveAsync(
            IReadOnlyList<OperationResult> results,
            CancellationToken cancellationToken = default)
        {
            Task? wait = null;
            lock (_gate)
            {
                if (_blockNextSave)
                {
                    _blockNextSave = false;
                    _saveStarted.TrySetResult();
                    wait = _releaseSave.Task;
                }
            }

            if (wait is not null)
            {
                await wait.WaitAsync(cancellationToken);
            }

            lock (_gate)
            {
                _results = results.ToArray();
            }
        }

        public void BlockNextSave()
        {
            lock (_gate)
            {
                _saveStarted = NewSource();
                _releaseSave = NewSource();
                _blockNextSave = true;
            }
        }

        public void ReleaseSave() => _releaseSave.TrySetResult();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => Now + TimeSpan.FromTicks(GetTimestamp());

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
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

    private sealed class FakeMsixClient(Func<PackageOperation, Task<OperationResult>> execute)
        : IMsixPackageOperationClient
    {
        public int CallCount { get; private set; }

        public async Task<OperationResult> UninstallAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return await execute(operation);
        }
    }

    private sealed class FakePrivilegedPackageOperationBroker : IPrivilegedPackageOperationBroker
    {
        private int _callCount;

        public bool IsAvailable { get; init; } = true;
        public int CallCount => Volatile.Read(ref _callCount);
        public ConcurrentQueue<PackageOperation> Operations { get; } = new();
        public CancellationToken LastCancellationToken { get; private set; }
        public Func<PackageOperation, IProgress<OperationProgress>?, CancellationToken, Task<OperationResult>> Execute { get; init; } =
            static (operation, _, _) => Task.FromResult(Success(operation) with
            {
                RanAsAdministrator = true
            });

        public Task<OperationResult> ExecuteElevatedAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Operations.Enqueue(operation);
            LastCancellationToken = cancellationToken;
            return Execute(operation, progress, cancellationToken);
        }
    }
}
