using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.App.Views;

/// <summary>
/// Adds transient queue feedback to a cached update row without changing the authoritative
/// update-discovery snapshot. Matching is limited to the exact WinGet package key.
/// </summary>
internal static class UpdateRowProjector
{
    internal static bool IsBulkActionEligible(PackageListItem item) =>
        item.IsActionEnabled && !item.RequiresAdministratorRetry;

    public static PackageListItem Apply(
        PackageListItem item,
        OperationQueueSnapshot queue,
        DateTimeOffset? lastSuccessfulCheckAt,
        bool mutationVerificationPending = false,
        bool restartRequiredThisBoot = false,
        bool mutationActionsAvailable = true,
        MutationVerificationPhase? mutationVerificationPhase = null,
        bool administratorRetryAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(queue);

        if (item.WingetPackage is not { } package)
        {
            return item;
        }

        item.OperationErrorKind = null;
        item.RequiresAdministratorRetry = false;

        var active = FindActiveOperation(queue, package);
        if (active is not null)
        {
            ApplyActiveState(
                item,
                active.Operation.Kind,
                active.Progress.State,
                active.Operation.RunAsAdministrator);
            return item;
        }

        // Durable recovery state is authoritative over replayed queue history. In
        // particular, an outcome-unknown marker must never be made actionable by a
        // wall-clock comparison after a crash or clock correction.
        if (restartRequiredThisBoot)
        {
            item.OperationState = PackageOperationState.RebootRequired;
            item.VerificationPhase = mutationVerificationPhase;
            (item.Status, item.ActionLabel, item.IsActionEnabled) =
                ("Restart Windows to verify the update result", "Restart required", false);
            item.StateGlyph = "\uE7BA";
            item.IsPositiveState = false;
            return item;
        }

        if (mutationVerificationPending)
        {
            item.OperationState = PackageOperationState.Completed;
            item.VerificationPhase = mutationVerificationPhase;
            (item.Status, item.ActionLabel, item.IsActionEnabled) =
                (mutationVerificationPhase switch
                {
                    MutationVerificationPhase.ApplicationRestartPending =>
                        "Completion unverified - close and reopen the app, then check again",
                    MutationVerificationPhase.VerificationPending =>
                        "Updated - verifying result...",
                    MutationVerificationPhase.RestartRequired =>
                        "Restart detected - verifying update result...",
                    _ => "Checking update result..."
                }, mutationVerificationPhase == MutationVerificationPhase.ApplicationRestartPending
                    ? "App restart needed"
                    : "Verifying", false);
            item.StateGlyph = "\uE895";
            item.IsPositiveState = false;
            return item;
        }

        var latestResult = queue.History
            .Where(result => IsMatchingOperation(result.EffectiveTarget, package)
                && AffectsUpdateRow(result))
            .OrderByDescending(result => result.CompletedAt)
            .FirstOrDefault();
        if (latestResult is not null
            && (RequiresAdministratorRetry(latestResult)
                || IsResultRelevant(latestResult, lastSuccessfulCheckAt)))
        {
            ApplyTerminalState(item, latestResult, administratorRetryAvailable);
        }

        if (!mutationActionsAvailable)
        {
            item.IsActionEnabled = false;
        }

        return item;
    }

    private static OperationQueueEntry? FindActiveOperation(
        OperationQueueSnapshot queue,
        PackageKey package)
    {
        if (queue.Current is { } current
            && IsMatchingOperation(current.Operation.EffectiveTarget, package))
        {
            return current;
        }

        return queue.Pending.FirstOrDefault(entry =>
            IsMatchingOperation(entry.Operation.EffectiveTarget, package));
    }

    private static bool IsMatchingOperation(OperationTarget? target, PackageKey package) =>
        target is WingetTarget wingetTarget
        && wingetTarget.Package == package;

    private static bool IsResultRelevant(
        OperationResult result,
        DateTimeOffset? lastSuccessfulCheckAt)
    {
        return lastSuccessfulCheckAt is null
            || result.CompletedAt > lastSuccessfulCheckAt.Value;
    }

    private static bool AffectsUpdateRow(OperationResult result) =>
        result.Kind == PackageOperationKind.Upgrade || result.IsSuccess;

    private static bool RequiresAdministratorRetry(OperationResult result) =>
        result.Kind == PackageOperationKind.Upgrade
        && result.State is PackageOperationState.Failed or PackageOperationState.Cancelled
        && (result.Error?.Kind == WingetErrorKind.AdministratorRequired
            || result.AdministratorRetryRequested);

    private static void ApplyActiveState(
        PackageListItem item,
        PackageOperationKind kind,
        PackageOperationState state,
        bool runAsAdministrator)
    {
        item.OperationState = state;
        item.IsActionEnabled = false;
        item.StateGlyph = state == PackageOperationState.Queued ? "\uE823" : "\uE895";
        item.IsPositiveState = false;
        if (kind == PackageOperationKind.Uninstall)
        {
            (item.Status, item.ActionLabel) = ("Uninstalling app...", "Busy");
            return;
        }

        if (kind == PackageOperationKind.Install)
        {
            (item.Status, item.ActionLabel) = ("Installing app...", "Busy");
            return;
        }

        (item.Status, item.ActionLabel) = state switch
        {
            PackageOperationState.Resolving when runAsAdministrator =>
                ("Waiting for Windows administrator approval...", "Admin approval"),
            PackageOperationState.Queued => ("Queued - waiting to start", "Queued"),
            PackageOperationState.Resolving => ("Preparing update...", "Preparing"),
            PackageOperationState.Downloading => ("Downloading update...", "Downloading"),
            PackageOperationState.Installing or PackageOperationState.Upgrading =>
                ("Installing update...", "Updating"),
            _ => ("Update in progress...", "Updating")
        };
    }

    private static void ApplyTerminalState(
        PackageListItem item,
        OperationResult result,
        bool administratorRetryAvailable)
    {
        if (result.Kind != PackageOperationKind.Upgrade
            && result.State is PackageOperationState.Failed or PackageOperationState.Cancelled)
        {
            return;
        }

        item.OperationState = result.State;
        item.OperationErrorKind = result.Error?.Kind;
        item.RequiresAdministratorRetry = RequiresAdministratorRetry(result);
        item.StateGlyph = result.State switch
        {
            PackageOperationState.Completed => "\uE73E",
            PackageOperationState.RebootRequired => "\uE7BA",
            PackageOperationState.Failed or PackageOperationState.Cancelled => "\uEA39",
            _ => "\uE895"
        };
        item.IsPositiveState = result.State == PackageOperationState.Completed;
        (item.Status, item.ActionLabel, item.IsActionEnabled) = result.State switch
        {
            PackageOperationState.Completed when result.Kind != PackageOperationKind.Upgrade =>
                ("Package changed - verifying result...", "Verifying", false),
            PackageOperationState.Completed =>
                ("Updated - verifying result...", "Verifying", false),
            PackageOperationState.RebootRequired =>
                ("Updated - restart required", "Restart required", false),
            PackageOperationState.Failed
                when result.Error?.Kind == WingetErrorKind.ApplicationInUse =>
                item.RequiresAdministratorRetry
                    ? administratorRetryAvailable
                        ? ("Close the app completely, then retry as administrator",
                            "Retry as administrator", true)
                        : ("Close the app completely - administrator retry unavailable",
                            "Admin required", false)
                    : ("Close the app completely, then retry the update", "Retry", true),
            PackageOperationState.Failed
                when result.Error?.Kind == WingetErrorKind.NoChangeDetected =>
                ("Installed version unchanged - close the app, then retry", "Retry", true),
            PackageOperationState.Failed or PackageOperationState.Cancelled
                when item.RequiresAdministratorRetry =>
                administratorRetryAvailable
                    ? (result.Error?.Kind == WingetErrorKind.ElevationDenied
                            ? "Administrator approval was canceled - elevated retry available"
                            : "Administrator approval required - elevated retry available",
                        "Retry as administrator", true)
                    : ("Administrator required - see Activity for details",
                        "Admin required", false),
            PackageOperationState.Failed =>
                ("Update failed - retry available", "Retry", true),
            PackageOperationState.Cancelled =>
                ("Update cancelled", "Retry", true),
            _ => (item.Status, item.ActionLabel, item.IsActionEnabled)
        };
    }
}
