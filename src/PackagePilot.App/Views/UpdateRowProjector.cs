using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.App.Views;

/// <summary>
/// Adds transient queue feedback to a cached update row without changing the authoritative
/// update-discovery snapshot. Matching is limited to the exact WinGet package key.
/// </summary>
internal static class UpdateRowProjector
{
    public static PackageListItem Apply(
        PackageListItem item,
        OperationQueueSnapshot queue,
        DateTimeOffset? lastSuccessfulCheckAt,
        bool mutationVerificationPending = false,
        bool restartRequiredThisBoot = false,
        bool mutationActionsAvailable = true,
        MutationVerificationPhase? mutationVerificationPhase = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(queue);

        if (item.WingetPackage is not { } package)
        {
            return item;
        }

        var active = FindActiveOperation(queue, package);
        if (active is not null)
        {
            ApplyActiveState(item, active.Operation.Kind, active.Progress.State);
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
            return item;
        }

        if (mutationVerificationPending)
        {
            item.OperationState = PackageOperationState.Completed;
            item.VerificationPhase = mutationVerificationPhase;
            (item.Status, item.ActionLabel, item.IsActionEnabled) =
                (mutationVerificationPhase switch
                {
                    MutationVerificationPhase.VerificationPending =>
                        "Updated - verifying result...",
                    MutationVerificationPhase.RestartRequired =>
                        "Restart detected - verifying update result...",
                    _ => "Checking update result..."
                }, "Verifying", false);
            return item;
        }

        var latestResult = queue.History
            .Where(result => IsMatchingOperation(result.EffectiveTarget, package)
                && AffectsUpdateRow(result)
                && IsResultRelevant(
                    result,
                    lastSuccessfulCheckAt))
            .OrderByDescending(result => result.CompletedAt)
            .FirstOrDefault();
        if (latestResult is not null)
        {
            ApplyTerminalState(item, latestResult);
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

    private static void ApplyActiveState(
        PackageListItem item,
        PackageOperationKind kind,
        PackageOperationState state)
    {
        item.OperationState = state;
        item.IsActionEnabled = false;
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
        OperationResult result)
    {
        if (result.Kind != PackageOperationKind.Upgrade
            && result.State is PackageOperationState.Failed or PackageOperationState.Cancelled)
        {
            return;
        }

        item.OperationState = result.State;
        (item.Status, item.ActionLabel, item.IsActionEnabled) = result.State switch
        {
            PackageOperationState.Completed when result.Kind != PackageOperationKind.Upgrade =>
                ("Package changed - verifying result...", "Verifying", false),
            PackageOperationState.Completed =>
                ("Updated - verifying result...", "Verifying", false),
            PackageOperationState.RebootRequired =>
                ("Updated - restart required", "Restart required", false),
            PackageOperationState.Failed =>
                ("Update failed - retry available", "Retry", true),
            PackageOperationState.Cancelled =>
                ("Update cancelled", "Retry", true),
            _ => (item.Status, item.ActionLabel, item.IsActionEnabled)
        };
    }
}
