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
    public void SuccessfulUpgrade_WithSameInstalledVersion_RequiresApplicationRestartAndAnotherCheck()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));

        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());
        var reconciliation = tracker.ReconcileUpgradeVerification(
            [target],
            [package],
            [package],
            isInstalledInventoryHealthy: true);

        Assert.True(reconciliation.StateChanged);
        Assert.Empty(reconciliation.Verified);
        Assert.Equal(target, Assert.Single(reconciliation.ApplicationRestartPending));
        Assert.Empty(reconciliation.Inconclusive);
        Assert.True(tracker.Contains(package.Key));
        Assert.True(tracker.HasApplicationRestartPending);
        Assert.False(tracker.IsRestartRequiredThisBoot(package.Key));
        Assert.Equal(
            MutationVerificationPhase.ApplicationRestartPending,
            tracker.GetPhase(package.Key, operation.Id));
        Assert.Equal(
            UpdateCheckReason.PackageMutation,
            tracker.GetEffectiveCheckReason(UpdateCheckReason.Manual));

        var nextTarget = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());
        var repeated = tracker.ReconcileUpgradeVerification(
            [nextTarget],
            [package],
            [package],
            isInstalledInventoryHealthy: true);
        Assert.False(repeated.StateChanged);
        Assert.Empty(repeated.ApplicationRestartPending);
        Assert.Equal(nextTarget, Assert.Single(repeated.NoChangeDetected));
        Assert.True(tracker.Contains(package.Key));

        var durableNoChange = Result(
            operation,
            PackageOperationState.Failed,
            RecordedAt.AddMinutes(3)) with
        {
            Error = new WingetError
            {
                Kind = WingetErrorKind.NoChangeDetected,
                Code = "InstalledVersionUnchanged",
                Message = "The installed version did not change."
            }
        };
        Assert.True(tracker.RecordResult(durableNoChange));
        Assert.False(tracker.Contains(package.Key));
    }

    [Fact]
    public void SuccessfulUpgrade_WhenExactUpdateDisappearsAndInstalledVersionAdvances_IsVerified()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));

        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());
        var installed = package with
        {
            InstalledVersion = "2.0",
            Status = PackageStatus.Installed
        };
        var reconciliation = tracker.ReconcileUpgradeVerification(
            targets: [target],
            currentUpdates: [],
            currentInstalled: [installed],
            isInstalledInventoryHealthy: true);

        Assert.True(reconciliation.StateChanged);
        Assert.Equal(target, Assert.Single(reconciliation.Verified));
        Assert.Empty(reconciliation.ApplicationRestartPending);
        Assert.False(tracker.Contains(package.Key));
    }

    [Fact]
    public void SuccessfulUpgrade_WhenInstalledInventoryIsUnhealthy_RemainsInconclusiveIfUpdateDisappears()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());

        var reconciliation = tracker.ReconcileUpgradeVerification(
            [target],
            currentUpdates: [],
            currentInstalled: [],
            isInstalledInventoryHealthy: false);

        Assert.False(reconciliation.StateChanged);
        Assert.Equal(target, Assert.Single(reconciliation.Inconclusive));
        Assert.True(tracker.Contains(package.Key));
    }

    [Fact]
    public void SuccessfulUpgrade_UpdateAbsenceAloneNeverProvesSuccess()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());

        var reconciliation = tracker.ReconcileUpgradeVerification(
            [target],
            currentUpdates: [],
            currentInstalled: [],
            isInstalledInventoryHealthy: true);

        Assert.False(reconciliation.StateChanged);
        Assert.Equal(target, Assert.Single(reconciliation.Inconclusive));
        Assert.True(tracker.Contains(package.Key));
    }

    [Fact]
    public void SuccessfulUpgrade_WhenInstalledVersionAdvances_IsVerifiedEvenWithNewerUpdate()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var newerUpdate = package with
        {
            InstalledVersion = "2.0",
            AvailableVersion = "3.0"
        };

        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());
        var reconciliation = tracker.ReconcileUpgradeVerification(
            [target],
            [newerUpdate],
            [newerUpdate],
            isInstalledInventoryHealthy: true);

        Assert.True(reconciliation.StateChanged);
        Assert.Equal(target, Assert.Single(reconciliation.Verified));
        Assert.False(tracker.Contains(package.Key));
    }

    [Fact]
    public void SuccessfulUpgrade_WithUnknownFreshVersion_RemainsInconclusive()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var unknownVersion = package with { InstalledVersion = null };

        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());
        var reconciliation = tracker.ReconcileUpgradeVerification(
            [target],
            [unknownVersion],
            currentInstalled: [],
            isInstalledInventoryHealthy: false);

        Assert.False(reconciliation.StateChanged);
        Assert.Equal(target, Assert.Single(reconciliation.Inconclusive));
        Assert.True(tracker.Contains(package.Key));
        Assert.Equal(
            MutationVerificationPhase.VerificationPending,
            tracker.GetPhase(package.Key, operation.Id));
    }

    [Fact]
    public void ApplicationRestartPending_RoundTripsAndStaysVerifiableInTheSameBoot()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var tracker = new MutationVerificationTracker(BootA);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());
        tracker.ReconcileUpgradeVerification(
            [target],
            [package],
            [package],
            isInstalledInventoryHealthy: true);

        var restored = new MutationVerificationTracker(BootA);
        restored.Import(tracker.CreateSnapshot());
        var restoredMarker = Assert.Single(restored.Export());

        Assert.False(restored.ReconcileHistory(
            [Result(operation, PackageOperationState.Completed, RecordedAt.AddMinutes(1))],
            [package]));

        Assert.True(restored.HasApplicationRestartPending);
        Assert.False(restored.IsRestartRequiredThisBoot(package.Key));
        Assert.Single(restored.CaptureVerificationTargetsForCurrentBoot());
        Assert.Equal(restoredMarker, Assert.Single(restored.Export()));
        Assert.Equal(
            MutationVerificationPhase.ApplicationRestartPending,
            restored.GetPhase(package.Key, operation.Id));
    }

    [Fact]
    public void ApplicationRestartPending_WithDurableNoChangeHistory_IsRepairedOnStartup()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var source = new MutationVerificationTracker(BootA);
        source.MarkEnqueued(operation, package);
        source.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var target = Assert.Single(source.CaptureVerificationTargetsForCurrentBoot());
        source.ReconcileUpgradeVerification(
            [target],
            [package],
            [package],
            isInstalledInventoryHealthy: true);

        var durableNoChange = Result(
            operation,
            PackageOperationState.Failed,
            RecordedAt.AddMinutes(2)) with
        {
            Error = new WingetError
            {
                Kind = WingetErrorKind.NoChangeDetected,
                Code = "InstalledVersionUnchanged",
                Message = "The installed version did not change."
            }
        };
        var restored = new MutationVerificationTracker(BootA);
        restored.Import(source.CreateSnapshot());

        Assert.True(restored.ReconcileHistory([durableNoChange], [package]));
        Assert.False(restored.Contains(package.Key));
        Assert.Empty(restored.CaptureVerificationTargetsForCurrentBoot());
    }

    [Theory]
    [InlineData(MutationVerificationPhase.OutcomeUnknown)]
    [InlineData(MutationVerificationPhase.VerificationPending)]
    public void DurableNoChangeHistory_RepairsAnOlderPersistedMarkerPhase(
        MutationVerificationPhase persistedPhase)
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var source = new MutationVerificationTracker(BootA);
        source.MarkEnqueued(operation, package);
        if (persistedPhase == MutationVerificationPhase.VerificationPending)
        {
            source.RecordResult(Result(
                operation,
                PackageOperationState.Completed,
                RecordedAt.AddMinutes(1)));
        }

        var noChange = Result(
            operation,
            PackageOperationState.Failed,
            RecordedAt.AddMinutes(3)) with
        {
            Error = new WingetError
            {
                Kind = WingetErrorKind.NoChangeDetected,
                Code = "InstalledVersionUnchanged",
                Message = "The installed version did not change."
            }
        };
        var restored = new MutationVerificationTracker(BootA);
        restored.Import(source.CreateSnapshot());

        Assert.Equal(persistedPhase, Assert.Single(restored.Export()).Phase);
        Assert.True(restored.ReconcileHistory([noChange], [package]));
        Assert.False(restored.Contains(package.Key));
    }

    [Fact]
    public void DurableNoChangeHistory_DoesNotOverrideASaferRestartRequiredMarker()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var source = new MutationVerificationTracker(BootA);
        source.MarkEnqueued(operation, package);
        source.RecordResult(Result(
            operation,
            PackageOperationState.RebootRequired,
            RecordedAt.AddMinutes(1)));
        var noChange = Result(
            operation,
            PackageOperationState.Failed,
            RecordedAt.AddMinutes(2)) with
        {
            Error = new WingetError
            {
                Kind = WingetErrorKind.NoChangeDetected,
                Code = "InstalledVersionUnchanged",
                Message = "The installed version did not change."
            }
        };
        var restored = new MutationVerificationTracker(BootA);
        restored.Import(source.CreateSnapshot());

        Assert.False(restored.ReconcileHistory([noChange], [package]));
        Assert.Equal(
            MutationVerificationPhase.RestartRequired,
            restored.GetPhase(package.Key, operation.Id));
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
    public void SuccessfulInstall_RemainsLockedUntilHealthyInventoryContainsPackage()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Install", "source-a");
        var operation = Operation(package, RecordedAt, PackageOperationKind.Install);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());

        Assert.False(tracker.ReconcileInstalledVerification(
            [target],
            [],
            isInstalledInventoryHealthy: true));
        Assert.True(tracker.Contains(package.Key));

        var installed = package with
        {
            Key = new PackageKey(
                package.Key.Id,
                WingetPackageIdentity.PredefinedInstalledSourceId),
            Status = PackageStatus.Installed
        };
        Assert.True(tracker.ReconcileInstalledVerification(
            [target],
            [installed],
            isInstalledInventoryHealthy: true));
        Assert.False(tracker.Contains(package.Key));
    }

    [Fact]
    public void SuccessfulInstall_RemainsLockedWhenInstalledInventoryIsUnhealthy()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Install", "source-a");
        var operation = Operation(package, RecordedAt, PackageOperationKind.Install);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());
        var installed = package with
        {
            Key = new PackageKey(
                package.Key.Id,
                WingetPackageIdentity.PredefinedInstalledSourceId),
            Status = PackageStatus.Installed
        };

        Assert.False(tracker.ReconcileInstalledVerification(
            [target],
            [installed],
            isInstalledInventoryHealthy: false));
        Assert.True(tracker.Contains(package.Key));
    }

    [Fact]
    public void SuccessfulUninstall_RemainsLockedUntilHealthyInventoryOmitsPackage()
    {
        var tracker = new MutationVerificationTracker(BootA);
        var package = Update("Contoso.Uninstall", "source-a");
        var operation = Operation(package, RecordedAt, PackageOperationKind.Uninstall);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var target = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());
        var stillInstalled = package with
        {
            Key = new PackageKey(
                package.Key.Id,
                WingetPackageIdentity.PredefinedInstalledSourceId),
            Status = PackageStatus.Installed
        };

        Assert.False(tracker.ReconcileInstalledVerification(
            [target],
            [stillInstalled],
            isInstalledInventoryHealthy: true));
        Assert.True(tracker.Contains(package.Key));

        Assert.True(tracker.ReconcileInstalledVerification(
            [target],
            [],
            isInstalledInventoryHealthy: true));
        Assert.False(tracker.Contains(package.Key));
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
    public void StaleTarget_CannotTriggerNoChangeReclassificationAfterRevisionChanges()
    {
        var package = Update("Contoso.Tool", "winget");
        var operation = Operation(package, RecordedAt);
        var tracker = new MutationVerificationTracker(BootA);
        tracker.MarkEnqueued(operation, package);
        tracker.RecordResult(Result(
            operation,
            PackageOperationState.Completed,
            RecordedAt.AddMinutes(1)));
        var staleTarget = Assert.Single(tracker.CaptureVerificationTargetsForCurrentBoot());

        var first = tracker.ReconcileUpgradeVerification(
            [staleTarget],
            [package],
            [package],
            isInstalledInventoryHealthy: true);
        Assert.True(first.StateChanged);

        var staleResult = tracker.ReconcileUpgradeVerification(
            [staleTarget],
            [package],
            [package],
            isInstalledInventoryHealthy: true);

        Assert.Empty(staleResult.NoChangeDetected);
        Assert.False(staleResult.StateChanged);
        Assert.True(tracker.HasApplicationRestartPending);
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
