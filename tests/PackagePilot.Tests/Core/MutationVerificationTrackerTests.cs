using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class MutationVerificationTrackerTests
{
    private const string BootA = "kuser-boot-v1:00000011";
    private const string BootB = "kuser-boot-v1:00000012";
    private static readonly DateTimeOffset RecordedAt =
        new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FailedVerification_ManualCheckRetriesAsMutationVerificationUntilSuccess()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(5)));

        Assert.Equal(
            UpdateCheckReason.PackageMutation,
            tracker.GetEffectiveCheckReason(UpdateCheckReason.Manual));
        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());

        // A failed scan does not call CompleteVerification. A later manual check must
        // remain a forced mutation-verification scan.
        Assert.Equal(
            UpdateCheckReason.PackageMutation,
            tracker.GetEffectiveCheckReason(UpdateCheckReason.Manual));
        Assert.True(tracker.CompleteVerification([target]));
        Assert.Equal(
            UpdateCheckReason.Manual,
            tracker.GetEffectiveCheckReason(UpdateCheckReason.Manual));
    }

    [Fact]
    public void UpdateVerificationTargets_ExcludeInstallAndUninstallMutations()
    {
        var upgrade = Update("Contoso.Upgrade", "winget");
        var install = Update("Contoso.Install", "winget");
        var uninstall = Update("Contoso.Uninstall", "winget");
        var operations = new[]
        {
            Operation(upgrade, RecordedAt, PackageOperationKind.Upgrade),
            Operation(install, RecordedAt, PackageOperationKind.Install),
            Operation(uninstall, RecordedAt, PackageOperationKind.Uninstall)
        };
        var packages = new[] { upgrade, install, uninstall };
        var tracker = new MutationVerificationTracker(BootA);

        for (var index = 0; index < operations.Length; index++)
        {
            tracker.MarkEnqueued(operations[index], packages[index]);
            tracker.RecordResult(Result(
                operations[index],
                PackageOperationState.Completed,
                RecordedAt.AddMinutes(index + 1)));
        }

        Assert.Equal(3, tracker.CaptureVerificationTargetsForCurrentBoot().Length);
        var updateTarget = Assert.Single(
            tracker.CaptureVerificationTargetsForCurrentBoot(PackageOperationKind.Upgrade));
        Assert.Equal(PackageOperationKind.Upgrade, updateTarget.Kind);

        Assert.True(tracker.CompleteVerification([updateTarget]));
        Assert.False(tracker.Contains(upgrade.Key));
        Assert.True(tracker.Contains(install.Key));
        Assert.True(tracker.Contains(uninstall.Key));
        Assert.Equal(
            UpdateCheckReason.Manual,
            tracker.GetEffectiveCheckReason(UpdateCheckReason.Manual));
    }

    [Fact]
    public void OutcomeUnknown_RemainsVisibleAndLockedForTheAdmissionBoot()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Tool", "winget");
        tracker.MarkEnqueued(Operation(package, RecordedAt), package);

        var marker = Assert.Single(tracker.Export());
        Assert.Equal(MutationVerificationPhase.OutcomeUnknown, marker.Phase);
        Assert.True(tracker.IsRestartRequiredThisBoot(package.Key));
        Assert.False(tracker.HasTargetsEligibleForVerification);
        Assert.Empty(tracker.CaptureVerificationTargetsForCurrentBoot());
        Assert.Equal(package, Assert.Single(tracker.GetPendingUpgradeVerifications()));
        Assert.Equal(package, Assert.Single(tracker.GetRestartRequiredUpdatesForCurrentBoot()));
    }

    [Fact]
    public void OutcomeUnknown_BecomesVerifiableOnlyWithADifferentBootIdentity()
    {
        var package = Update("Contoso.Tool", "winget");
        var firstBoot = new MutationVerificationTracker(BootA);
        firstBoot.MarkEnqueued(Operation(package, RecordedAt), package);

        var sameBootAfterArbitraryClockChange = new MutationVerificationTracker(BootA);
        sameBootAfterArbitraryClockChange.Import(firstBoot.CreateSnapshot());
        Assert.False(sameBootAfterArbitraryClockChange.HasTargetsEligibleForVerification);

        var nextBoot = new MutationVerificationTracker(BootB);
        nextBoot.Import(firstBoot.CreateSnapshot());
        Assert.True(nextBoot.HasTargetsEligibleForVerification);
        Assert.False(nextBoot.IsRestartRequiredThisBoot(package.Key));
        Assert.Equal(
            package.Key,
            Assert.Single(nextBoot.CaptureVerificationTargetsForCurrentBoot()).Package);
    }

    [Fact]
    public void RebootMarker_RoundTripsAndSurvivesClearedHistoryDuringSameBoot()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var tracker = new MutationVerificationTracker(BootA);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.RebootRequired,
            RecordedAt.AddMinutes(5)));

        var restored = new MutationVerificationTracker(BootA);
        restored.Import(tracker.CreateSnapshot());
        restored.ReconcileHistory([], []);

        Assert.Equal(
            MutationVerificationPhase.RestartRequired,
            Assert.Single(restored.Export()).Phase);
        Assert.True(restored.IsRestartRequiredThisBoot(package.Key));
        Assert.False(restored.HasTargetsEligibleForVerification);
        Assert.Equal(package, Assert.Single(restored.GetRestartRequiredUpdatesForCurrentBoot()));
    }

    [Fact]
    public void RebootMarker_AfterActualRestartRequiresPostBootVerification()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var firstBoot = new MutationVerificationTracker(BootA);
        firstBoot.MarkEnqueued(operation, package);
        firstBoot.RecordResult(Result(
            operation,
            PackageOperationState.RebootRequired,
            RecordedAt.AddMinutes(5)));

        var nextBoot = new MutationVerificationTracker(BootB);
        nextBoot.Import(firstBoot.CreateSnapshot());

        Assert.False(nextBoot.IsRestartRequiredThisBoot(package.Key));
        var target = Assert.Single(nextBoot.CaptureVerificationTargetsForCurrentBoot());
        Assert.True(nextBoot.CompleteVerification([target]));
        Assert.False(nextBoot.Contains(package.Key));
    }

    [Theory]
    [InlineData(PackageOperationState.Completed)]
    [InlineData(PackageOperationState.RebootRequired)]
    [InlineData(PackageOperationState.Failed)]
    [InlineData(PackageOperationState.Cancelled)]
    public void WrongOperationId_CannotTransitionOrRemoveMarker(
        PackageOperationState conflictingState)
    {
        var package = Update("Contoso.Tool", "winget");
        var current = Operation(package, RecordedAt.AddHours(1));
        var other = Operation(package, RecordedAt.AddHours(2));
        var tracker = new MutationVerificationTracker(BootA);
        tracker.MarkEnqueued(current, package);
        var original = Assert.Single(tracker.Export());

        Assert.False(tracker.RecordResult(Result(
            other,
            conflictingState,
            RecordedAt.AddHours(3))));
        Assert.Equal(original, Assert.Single(tracker.Export()));

        tracker.ReconcileHistory(
            [Result(other, conflictingState, RecordedAt.AddHours(4))],
            [package]);
        Assert.Equal(original, Assert.Single(tracker.Export()));
    }

    [Fact]
    public void TerminalTransitions_AreMonotonicAndChooseTheSaferConflict()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var tracker = new MutationVerificationTracker(BootA);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));

        Assert.False(tracker.RecordResult(Result(
            operation,
            PackageOperationState.Failed,
            RecordedAt.AddMinutes(2))));
        Assert.Equal(
            MutationVerificationPhase.VerificationPending,
            Assert.Single(tracker.Export()).Phase);

        Assert.True(tracker.RecordResult(Result(
            operation,
            PackageOperationState.RebootRequired,
            RecordedAt.AddMinutes(3))));
        Assert.Equal(
            MutationVerificationPhase.RestartRequired,
            Assert.Single(tracker.Export()).Phase);

        Assert.False(tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(4))));
        Assert.Equal(
            MutationVerificationPhase.RestartRequired,
            Assert.Single(tracker.Export()).Phase);
    }

    [Fact]
    public void OldScanCompletion_CannotClearAChangedMarkerRevision()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var tracker = new MutationVerificationTracker(BootA);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var captured = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());

        tracker.RecordResult(Result(
            operation,
            PackageOperationState.RebootRequired,
            RecordedAt.AddMinutes(2)));

        Assert.False(tracker.CompleteVerification([captured]));
        Assert.True(tracker.Contains(package.Key));
        Assert.Equal(
            MutationVerificationPhase.RestartRequired,
            Assert.Single(tracker.Export()).Phase);
    }

    [Fact]
    public void ReconcileHistory_RecoversUntrackedSuccessConservatively()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var tracker = new MutationVerificationTracker(BootA);

        Assert.True(tracker.ReconcileHistory(
            [Result(operation, PackageOperationState.Completed, RecordedAt.AddMinutes(1))],
            [package]));

        var marker = Assert.Single(tracker.Export());
        Assert.Equal(MutationVerificationPhase.OutcomeUnknown, marker.Phase);
        Assert.True(tracker.IsRestartRequiredThisBoot(package.Key));
        Assert.False(tracker.HasTargetsEligibleForVerification);
    }

    [Fact]
    public void InitialHistoryBaseline_PreventsLegacySuccessRecovery()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var result = Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1));
        var tracker = new MutationVerificationTracker(BootA);

        tracker.SeedVerifiedOperations([result]);
        Assert.False(tracker.ReconcileHistory([result], [package]));
        Assert.Empty(tracker.Export());

        var restored = new MutationVerificationTracker(BootA);
        restored.Import(tracker.CreateSnapshot());
        Assert.False(restored.ReconcileHistory([result], [package]));
    }

    [Fact]
    public void ReplayedSuccess_DoesNotCreateMarkerOutsideExplicitHistoryReconciliation()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var tracker = new MutationVerificationTracker(BootA);

        Assert.False(tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1))));
        Assert.False(tracker.Contains(package.Key));
    }

    [Fact]
    public void NonTerminalHistoryState_CannotRemoveAnOutcomeUnknownMarker()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var tracker = new MutationVerificationTracker(BootA);
        tracker.MarkEnqueued(operation, package);

        Assert.False(tracker.RecordResult(Result(
            operation,
            PackageOperationState.Queued,
            RecordedAt.AddMinutes(1))));
        Assert.Equal(
            MutationVerificationPhase.OutcomeUnknown,
            Assert.Single(tracker.Export()).Phase);
    }

    [Fact]
    public void ElevatedTransportOutcomeUnknown_PreservesRecoveryLockUntilRestartVerification()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt) with
        {
            RunAsAdministrator = true
        };
        var tracker = new MutationVerificationTracker(BootA);
        tracker.MarkEnqueued(operation, package);
        var transportFailure = Result(
            operation,
            PackageOperationState.Failed,
            RecordedAt.AddMinutes(5)) with
        {
            RanAsAdministrator = true,
            Error = new WingetError
            {
                Kind = WingetErrorKind.OutcomeUnknown,
                Code = "PackageAdminResponseLost",
                Message = "The administrator helper started the package operation, but its final result was not received."
            }
        };

        Assert.False(tracker.RecordResult(transportFailure));
        Assert.True(tracker.Contains(package.Key));
        Assert.Equal(
            MutationVerificationPhase.OutcomeUnknown,
            Assert.Single(tracker.Export()).Phase);
        Assert.True(tracker.IsRestartRequiredThisBoot(package.Key));
        Assert.False(tracker.HasTargetsEligibleForVerification);
    }

    [Fact]
    public void TargetsRemainExactAcrossSources()
    {
        var winget = Update("Contoso.Tool", "winget");
        var privateSource = Update("Contoso.Tool", "private");
        var tracker = new MutationVerificationTracker(BootA);
        tracker.MarkEnqueued(Operation(winget, RecordedAt), winget);

        Assert.True(tracker.Contains(winget.Key));
        Assert.False(tracker.Contains(privateSource.Key));
    }

    [Fact]
    public void MissingBootIdentity_FailsClosedForUnknownAndRestartMarkers()
    {
        var package = Update("Contoso.Tool", "winget");
        var source = new MutationVerificationTracker(BootA);
        source.MarkEnqueued(Operation(package, RecordedAt), package);
        var tracker = new MutationVerificationTracker(currentBootSessionId: null);
        tracker.Import(source.CreateSnapshot());

        Assert.False(tracker.CanAdmitMutations);
        Assert.True(tracker.IsRestartRequiredThisBoot(package.Key));
        Assert.False(tracker.HasTargetsEligibleForVerification);
    }

    private static PackageSummary Update(string id, string source) => new()
    {
        Key = new PackageKey(id, source),
        Name = id,
        SourceName = source,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
        Status = PackageStatus.UpdateAvailable
    };

    private static PackageOperation Operation(
        PackageSummary package,
        DateTimeOffset enqueuedAt,
        PackageOperationKind kind = PackageOperationKind.Upgrade) =>
        PackageOperation.Create(kind, package.Key, package.Name) with
        {
            EnqueuedAt = enqueuedAt
        };

    private static OperationResult Result(
        PackageOperation operation,
        PackageOperationState state,
        DateTimeOffset completedAt) => new()
        {
            OperationId = operation.Id,
            Package = operation.Package,
            Target = operation.Target,
            Kind = operation.Kind,
            State = state,
            StartedAt = operation.EnqueuedAt,
            CompletedAt = completedAt,
            RebootRequired = state == PackageOperationState.RebootRequired
        };
}
