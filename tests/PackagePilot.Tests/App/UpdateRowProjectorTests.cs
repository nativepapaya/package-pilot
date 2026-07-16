using PackagePilot.App.Views;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.App;

public sealed class UpdateRowProjectorTests
{
    private static readonly DateTimeOffset LastSuccessfulCheck =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AdministratorRetry_IsNeverEligibleForBulkUpdate()
    {
        var row = new PackageListItem
        {
            IsActionEnabled = true,
            RequiresAdministratorRetry = true
        };

        Assert.False(UpdateRowProjector.IsBulkActionEligible(row));
    }
    [Fact]
    public void QueuedUpgrade_DisablesOnlyTheExactPackageKey()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var queue = new OperationQueueSnapshot
        {
            Pending = [Entry(package, PackageOperationState.Queued)]
        };

        var matching = UpdateRowProjector.Apply(
            Row(package),
            queue,
            LastSuccessfulCheck);
        var otherSource = UpdateRowProjector.Apply(
            Row(new PackageKey(package.Id, "store")),
            queue,
            LastSuccessfulCheck);

        Assert.Equal(PackageOperationState.Queued, matching.OperationState);
        Assert.Equal("Queued - waiting to start", matching.Status);
        Assert.Equal("Queued", matching.ActionLabel);
        Assert.False(matching.IsActionEnabled);

        Assert.Null(otherSource.OperationState);
        Assert.Equal("UpdateAvailable", otherSource.Status);
        Assert.Equal("Update", otherSource.ActionLabel);
        Assert.True(otherSource.IsActionEnabled);
    }

    [Theory]
    [InlineData(PackageOperationState.Resolving, "Preparing update...", "Preparing")]
    [InlineData(PackageOperationState.Downloading, "Downloading update...", "Downloading")]
    [InlineData(PackageOperationState.Installing, "Installing update...", "Updating")]
    [InlineData(PackageOperationState.Upgrading, "Installing update...", "Updating")]
    public void RunningUpgrade_ShowsTransitionFeedbackWithoutPercentChurn(
        PackageOperationState state,
        string expectedStatus,
        string expectedAction)
    {
        var package = new PackageKey("Contoso.App", "winget");
        var queue = new OperationQueueSnapshot
        {
            Current = Entry(package, state, percent: 42)
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            LastSuccessfulCheck);

        Assert.Equal(state, row.OperationState);
        Assert.Equal(expectedStatus, row.Status);
        Assert.Equal(expectedAction, row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Fact]
    public void CompletedUpgrade_RemainsDisabledUntilASuccessfulVerificationCheck()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var result = Result(package, PackageOperationState.Completed, LastSuccessfulCheck.AddMinutes(1));
        var queue = new OperationQueueSnapshot { History = [result] };

        var awaitingVerification = UpdateRowProjector.Apply(
            Row(package),
            queue,
            LastSuccessfulCheck);
        var verifiedSnapshotStillHasANewerUpdate = UpdateRowProjector.Apply(
            Row(package),
            queue,
            result.CompletedAt.AddMinutes(1));
        var staleConcurrentSnapshot = UpdateRowProjector.Apply(
            Row(package),
            queue,
            result.CompletedAt.AddMinutes(1),
            mutationVerificationPending: true);

        Assert.Equal(PackageOperationState.Completed, awaitingVerification.OperationState);
        Assert.Equal("Updated - verifying result...", awaitingVerification.Status);
        Assert.Equal("Verifying", awaitingVerification.ActionLabel);
        Assert.False(awaitingVerification.IsActionEnabled);

        Assert.Null(verifiedSnapshotStillHasANewerUpdate.OperationState);
        Assert.Equal("Update", verifiedSnapshotStillHasANewerUpdate.ActionLabel);
        Assert.True(verifiedSnapshotStillHasANewerUpdate.IsActionEnabled);

        Assert.Equal(PackageOperationState.Completed, staleConcurrentSnapshot.OperationState);
        Assert.Equal("Verifying", staleConcurrentSnapshot.ActionLabel);
        Assert.False(staleConcurrentSnapshot.IsActionEnabled);
    }

    [Theory]
    [InlineData(PackageOperationState.Failed, "Update failed - retry available")]
    [InlineData(PackageOperationState.Cancelled, "Update cancelled")]
    public void FailedOrCancelledUpgrade_OffersAnExplicitSafeRetry(
        PackageOperationState state,
        string expectedStatus)
    {
        var package = new PackageKey("Contoso.App", "winget");
        var queue = new OperationQueueSnapshot
        {
            History = [Result(package, state, LastSuccessfulCheck.AddMinutes(1))]
        };
        var row = Row(package);

        UpdateRowProjector.Apply(
            row,
            queue,
            LastSuccessfulCheck);

        Assert.Equal(state, row.OperationState);
        Assert.Equal(expectedStatus, row.Status);
        Assert.Equal("Retry", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
        Assert.Equal(PackageOperationKind.Upgrade, row.RequestedOperationKind);
    }

    [Fact]
    public void PackagedServiceAdministratorFailure_DoesNotOfferAnIneffectiveRetry()
    {
        var package = new PackageKey("Contoso.ServiceApp", "winget");
        var failed = Result(
            package,
            PackageOperationState.Failed,
            LastSuccessfulCheck.AddMinutes(1)) with
        {
            Error = new WingetError
            {
                Kind = WingetErrorKind.AdministratorRequired,
                Code = "InstallError:-2147001048",
                Message = "Windows requires administrator privileges.",
                HResult = unchecked((int)0x80073D28)
            }
        };
        var queue = new OperationQueueSnapshot { History = [failed] };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            LastSuccessfulCheck);

        Assert.Equal(PackageOperationState.Failed, row.OperationState);
        Assert.Equal(WingetErrorKind.AdministratorRequired, row.OperationErrorKind);
        Assert.Equal("Administrator required - see Activity for details", row.Status);
        Assert.Equal("Admin required", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
        Assert.True(row.RequiresAdministratorRetry);
    }

    [Fact]
    public void PackagedServiceAdministratorFailure_OffersExplicitBrokerRetryWhenAvailable()
    {
        var package = new PackageKey("Contoso.ServiceApp", "winget");
        var failed = Result(
            package,
            PackageOperationState.Failed,
            LastSuccessfulCheck.AddMinutes(1)) with
        {
            Error = new WingetError
            {
                Kind = WingetErrorKind.AdministratorRequired,
                Code = "InstallError:-2147001048",
                Message = "Windows requires administrator privileges.",
                HResult = unchecked((int)0x80073D28)
            }
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            new OperationQueueSnapshot { History = [failed] },
            LastSuccessfulCheck,
            administratorRetryAvailable: true);

        Assert.Equal("Retry as administrator", row.ActionLabel);
        Assert.Equal("Administrator approval required - elevated retry available", row.Status);
        Assert.True(row.IsActionEnabled);
        Assert.True(row.RequiresAdministratorRetry);
        Assert.Equal(PackageOperationKind.Upgrade, row.RequestedOperationKind);
    }

    [Fact]
    public void CancelledAdministratorApproval_PreservesExplicitAdministratorRetry()
    {
        var package = new PackageKey("Contoso.ServiceApp", "winget");
        var failed = Result(
            package,
            PackageOperationState.Failed,
            LastSuccessfulCheck.AddMinutes(1)) with
        {
            AdministratorRetryRequested = true,
            Error = new WingetError
            {
                Kind = WingetErrorKind.ElevationDenied,
                Code = "ElevationCancelled",
                Message = "Administrator approval was canceled."
            }
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            new OperationQueueSnapshot { History = [failed] },
            LastSuccessfulCheck,
            administratorRetryAvailable: true);

        Assert.Equal(PackageOperationState.Failed, row.OperationState);
        Assert.Equal(WingetErrorKind.ElevationDenied, row.OperationErrorKind);
        Assert.Equal("Administrator approval was canceled - elevated retry available", row.Status);
        Assert.Equal("Retry as administrator", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
        Assert.True(row.RequiresAdministratorRetry);
        Assert.Equal(PackageOperationKind.Upgrade, row.RequestedOperationKind);
    }

    [Fact]
    public void PackagedServiceAdministratorFailure_RemainsDisabledAfterSuccessfulRefresh()
    {
        var package = new PackageKey("Contoso.ServiceApp", "winget");
        var failed = Result(
            package,
            PackageOperationState.Failed,
            LastSuccessfulCheck.AddMinutes(1)) with
        {
            Error = new WingetError
            {
                Kind = WingetErrorKind.AdministratorRequired,
                Code = "InstallError:-2147001048",
                Message = "Windows requires administrator privileges.",
                HResult = unchecked((int)0x80073D28)
            }
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            new OperationQueueSnapshot { History = [failed] },
            failed.CompletedAt.AddMinutes(1));

        Assert.Equal(PackageOperationState.Failed, row.OperationState);
        Assert.Equal(WingetErrorKind.AdministratorRequired, row.OperationErrorKind);
        Assert.Equal("Admin required", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Fact]
    public void RecoveryStoreFailure_DisablesRetryUntilDurabilityIsRestored()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var queue = new OperationQueueSnapshot
        {
            History =
            [
                Result(
                    package,
                    PackageOperationState.Failed,
                    LastSuccessfulCheck.AddMinutes(1))
            ]
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            LastSuccessfulCheck,
            mutationActionsAvailable: false);

        Assert.Equal("Retry", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Fact]
    public void RebootRequiredUpgrade_RemainsLockedAfterVerificationUntilWindowsRestarts()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var result = Result(
            package,
            PackageOperationState.RebootRequired,
            LastSuccessfulCheck.AddMinutes(1));
        var queue = new OperationQueueSnapshot
        {
            History = [result]
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            lastSuccessfulCheckAt: result.CompletedAt.AddMinutes(1),
            mutationVerificationPending: true,
            restartRequiredThisBoot: true,
            mutationVerificationPhase: MutationVerificationPhase.RestartRequired);

        Assert.Equal("Restart Windows to verify the update result", row.Status);
        Assert.Equal("Restart required", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Fact]
    public void RebootRequiredUpgrade_VerifiesBeforeUnlockingAfterWindowsRestarts()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var result = Result(
            package,
            PackageOperationState.RebootRequired,
            LastSuccessfulCheck.AddMinutes(1));
        var queue = new OperationQueueSnapshot { History = [result] };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            lastSuccessfulCheckAt: result.CompletedAt.AddMinutes(1),
            mutationVerificationPending: true,
            mutationVerificationPhase: MutationVerificationPhase.RestartRequired);

        Assert.Equal(PackageOperationState.Completed, row.OperationState);
        Assert.Equal("Restart detected - verifying update result...", row.Status);
        Assert.Equal("Verifying", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Fact]
    public void RebootRequiredUpgrade_UnlocksAfterPostRestartVerification()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var result = Result(
            package,
            PackageOperationState.RebootRequired,
            LastSuccessfulCheck.AddMinutes(1));
        var queue = new OperationQueueSnapshot { History = [result] };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            lastSuccessfulCheckAt: result.CompletedAt.AddMinutes(1));

        Assert.Null(row.OperationState);
        Assert.Equal("Update", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
    }

    [Fact]
    public void PersistedRebootMarker_KeepsSyntheticRowLockedWithoutActivityHistory()
    {
        var package = new PackageKey("Contoso.App", "winget");

        var row = UpdateRowProjector.Apply(
            Row(package),
            new OperationQueueSnapshot(),
            LastSuccessfulCheck,
            mutationVerificationPending: true,
            restartRequiredThisBoot: true,
            mutationVerificationPhase: MutationVerificationPhase.RestartRequired);

        Assert.Equal(PackageOperationState.RebootRequired, row.OperationState);
        Assert.Equal("Restart Windows to verify the update result", row.Status);
        Assert.Equal("Restart required", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Fact]
    public void PersistedOrdinaryMarker_KeepsRowLockedWithoutActivityHistory()
    {
        var package = new PackageKey("Contoso.App", "winget");

        var row = UpdateRowProjector.Apply(
            Row(package),
            new OperationQueueSnapshot(),
            LastSuccessfulCheck,
            mutationVerificationPending: true,
            mutationVerificationPhase: MutationVerificationPhase.VerificationPending);

        Assert.Equal(PackageOperationState.Completed, row.OperationState);
        Assert.Equal("Updated - verifying result...", row.Status);
        Assert.Equal("Verifying", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Fact]
    public void OutcomeUnknownMarker_UsesNeutralRecoveryWordingAfterRestart()
    {
        var package = new PackageKey("Contoso.App", "winget");

        var row = UpdateRowProjector.Apply(
            Row(package),
            new OperationQueueSnapshot(),
            LastSuccessfulCheck,
            mutationVerificationPending: true,
            mutationVerificationPhase: MutationVerificationPhase.OutcomeUnknown);

        Assert.Equal(PackageOperationState.Completed, row.OperationState);
        Assert.Equal("Checking update result...", row.Status);
        Assert.Equal("Verifying", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Theory]
    [InlineData(PackageOperationKind.Install, "Installing app...")]
    [InlineData(PackageOperationKind.Uninstall, "Uninstalling app...")]
    public void OtherActiveMutationForExactTarget_DisablesTheUpdateAction(
        PackageOperationKind kind,
        string expectedStatus)
    {
        var package = new PackageKey("Contoso.App", "winget");
        var queue = new OperationQueueSnapshot
        {
            Current = Entry(package, PackageOperationState.Installing, kind: kind)
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            LastSuccessfulCheck);

        Assert.Equal(expectedStatus, row.Status);
        Assert.Equal("Busy", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Fact]
    public void LatestMatchingTerminalResultWins()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var queue = new OperationQueueSnapshot
        {
            History =
            [
                Result(package, PackageOperationState.Completed, LastSuccessfulCheck.AddMinutes(2)),
                Result(package, PackageOperationState.Failed, LastSuccessfulCheck.AddMinutes(1))
            ]
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            LastSuccessfulCheck);

        Assert.Equal(PackageOperationState.Completed, row.OperationState);
        Assert.Equal("Verifying", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    [Fact]
    public void FailedNonUpgradeMutation_DoesNotClaimTheUpdateFailed()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var queue = new OperationQueueSnapshot
        {
            History =
            [
                Result(
                    package,
                    PackageOperationState.Failed,
                    LastSuccessfulCheck.AddMinutes(1),
                    PackageOperationKind.Uninstall)
            ]
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            LastSuccessfulCheck);

        Assert.Null(row.OperationState);
        Assert.Equal("Update", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
    }

    [Fact]
    public void NewerFailedNonUpgrade_DoesNotMaskUnverifiedSuccessfulUpgrade()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var queue = new OperationQueueSnapshot
        {
            History =
            [
                Result(
                    package,
                    PackageOperationState.Failed,
                    LastSuccessfulCheck.AddMinutes(2),
                    PackageOperationKind.Uninstall),
                Result(
                    package,
                    PackageOperationState.Completed,
                    LastSuccessfulCheck.AddMinutes(1))
            ]
        };

        var row = UpdateRowProjector.Apply(
            Row(package),
            queue,
            LastSuccessfulCheck,
            mutationVerificationPending: true);

        Assert.Equal(PackageOperationState.Completed, row.OperationState);
        Assert.Equal("Verifying", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
    }

    private static PackageListItem Row(PackageKey package) => new()
    {
        Name = "Contoso App",
        PackageId = package.Id,
        Source = package.SourceId,
        Status = "UpdateAvailable",
        ActionLabel = "Update",
        RequestedOperationKind = PackageOperationKind.Upgrade,
        WingetPackage = package
    };

    private static OperationQueueEntry Entry(
        PackageKey package,
        PackageOperationState state,
        double? percent = null,
        PackageOperationKind kind = PackageOperationKind.Upgrade)
    {
        var operation = PackageOperation.Create(
            kind,
            package,
            "Contoso App");
        return new OperationQueueEntry(
            operation,
            new OperationProgress
            {
                OperationId = operation.Id,
                State = state,
                Percent = percent
            });
    }

    private static OperationResult Result(
        PackageKey package,
        PackageOperationState state,
        DateTimeOffset completedAt,
        PackageOperationKind kind = PackageOperationKind.Upgrade) =>
        new()
        {
            OperationId = Guid.NewGuid(),
            Package = package,
            Target = new WingetTarget { Package = package },
            Kind = kind,
            State = state,
            CompletedAt = completedAt
        };
}
