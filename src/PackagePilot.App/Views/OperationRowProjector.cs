using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.App.Views;

internal static class OperationRowProjector
{
    public static bool HaveSamePresentation(
        OperationListItem left,
        OperationListItem right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.OperationId == right.OperationId
            && string.Equals(left.PackageName, right.PackageName, StringComparison.Ordinal)
            && string.Equals(left.PackageId, right.PackageId, StringComparison.Ordinal)
            && string.Equals(left.Action, right.Action, StringComparison.Ordinal)
            && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
            && string.Equals(left.Detail, right.Detail, StringComparison.Ordinal)
            && string.Equals(left.Timestamp, right.Timestamp, StringComparison.Ordinal)
            && left.Progress.Equals(right.Progress)
            && left.IsActive == right.IsActive
            && left.IsHistory == right.IsHistory
            && left.IsIndeterminate == right.IsIndeterminate
            && left.ShowProgress == right.ShowProgress
            && left.CanCancel == right.CanCancel
            && left.ShowCancel == right.ShowCancel
            && left.CanViewDiagnostic == right.CanViewDiagnostic
            && left.IsLiveDiagnostic == right.IsLiveDiagnostic
            && left.IsVerificationPending == right.IsVerificationPending
            && left.VerificationPhase == right.VerificationPhase
            && string.Equals(
                left.DiagnosticProviderLabel,
                right.DiagnosticProviderLabel,
                StringComparison.Ordinal)
            && string.Equals(
                left.DiagnosticAutomationName,
                right.DiagnosticAutomationName,
                StringComparison.Ordinal)
            && string.Equals(
                left.DiagnosticToolTip,
                right.DiagnosticToolTip,
                StringComparison.Ordinal);
    }

    public static void CopyPresentation(OperationListItem target, OperationListItem source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        target.Status = source.Status;
        target.Detail = source.Detail;
        target.Timestamp = source.Timestamp;
        target.Progress = source.Progress;
        target.IsActive = source.IsActive;
        target.IsHistory = source.IsHistory;
        target.IsIndeterminate = source.IsIndeterminate;
        target.ShowProgress = source.ShowProgress;
        target.CanCancel = source.CanCancel;
        target.ShowCancel = source.ShowCancel;
        target.CanViewDiagnostic = source.CanViewDiagnostic;
        target.IsLiveDiagnostic = source.IsLiveDiagnostic;
        target.IsVerificationPending = source.IsVerificationPending;
        target.VerificationPhase = source.VerificationPhase;
        target.DiagnosticProviderLabel = source.DiagnosticProviderLabel;
        target.DiagnosticAutomationName = source.DiagnosticAutomationName;
        target.DiagnosticToolTip = source.DiagnosticToolTip;
    }

    public static OperationListItem FromEntry(OperationQueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var operation = entry.Operation;
        var packageId = operation.EffectiveTarget?.Id ?? operation.Package.Id;
        var providerLabel = GetProviderLabel(operation.Package, operation.EffectiveTarget);
        var canViewDiagnostic = operation.EffectiveTarget is WingetTarget or MsixTarget;
        var isActive = entry.Progress.State is PackageOperationState.Resolving
            or PackageOperationState.Downloading
            or PackageOperationState.Installing
            or PackageOperationState.Upgrading
            or PackageOperationState.Uninstalling;
        var progressMessage = string.IsNullOrWhiteSpace(entry.Progress.Message)
            ? null
            : entry.Progress.Message;
        var liveDetail = progressMessage is not null
            ? entry.Progress.Percent is { } reportedPercent
                ? $"{progressMessage} - {reportedPercent:0.#}%"
                : progressMessage
            : entry.Progress.State == PackageOperationState.Queued
                ? "Live log viewer ready; provider output will appear when work starts."
                : entry.Progress.Percent is { } percent
                    ? $"Live progress: {percent:0.#}%"
                    : entry.Progress.CanCancel
                        ? "Live operation status; waiting for the next provider update."
                        : "Live operation status; the installer controls completion.";

        return new OperationListItem
        {
            OperationId = operation.Id,
            PackageName = operation.DisplayName,
            PackageId = packageId,
            Action = operation.Kind.ToString(),
            Status = entry.Progress.State.ToString(),
            Detail = liveDetail,
            Timestamp = entry.Progress.Timestamp.LocalDateTime.ToString("g"),
            Progress = entry.Progress.Percent ?? 0,
            IsActive = isActive,
            IsIndeterminate = entry.Progress.Percent is null
                && entry.Progress.State is not PackageOperationState.Queued,
            ShowProgress = isActive,
            CanCancel = entry.Progress.CanCancel,
            ShowCancel = entry.Progress.CanCancel,
            CanViewDiagnostic = canViewDiagnostic,
            IsLiveDiagnostic = canViewDiagnostic,
            DiagnosticProviderLabel = providerLabel,
            DiagnosticAutomationName = canViewDiagnostic
                ? $"View live {providerLabel} diagnostics for {packageId}"
                : string.Empty,
            DiagnosticToolTip = canViewDiagnostic
                ? $"Live {providerLabel} diagnostics"
                : string.Empty
        };
    }

    public static OperationListItem FromResult(
        OperationResult result,
        MutationVerificationPhase? verificationPhase = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var packageId = result.EffectiveTarget?.Id ?? result.Package.Id;
        var diagnostic = result.EffectiveDiagnostic;
        var providerLabel = GetProviderLabel(result, diagnostic);
        var hasUnresolvedVerification = verificationPhase is not null;
        var isPendingVerification = result.IsSuccess && hasUnresolvedVerification;
        var status = isPendingVerification
            ? verificationPhase switch
            {
                MutationVerificationPhase.ApplicationRestartPending => "App restart needed",
                MutationVerificationPhase.RestartRequired => "Restart required",
                _ => "Verifying"
            }
            : result.State.ToString();
        var detail = isPendingVerification
            ? verificationPhase switch
            {
                MutationVerificationPhase.ApplicationRestartPending =>
                    "WinGet reported completion, but Windows still reports the previous version. If the app was open, close and reopen it, then check again.",
                MutationVerificationPhase.RestartRequired =>
                    "Restart Windows to complete this operation, then check again.",
                _ =>
                    "WinGet finished; Package Pilot is checking the installed package state."
            }
            : result.Error?.Message
                ?? (result.RebootRequired
                    ? "Restart Windows to complete this operation."
                    : "Completed");
        return new OperationListItem
        {
            OperationId = result.OperationId,
            PackageName = string.IsNullOrWhiteSpace(result.DisplayName)
                ? packageId
                : result.DisplayName,
            PackageId = packageId,
            Action = result.Kind.ToString(),
            Status = status,
            Detail = detail,
            Timestamp = result.CompletedAt.LocalDateTime.ToString("g"),
            Progress = isPendingVerification ? 95 : result.IsSuccess ? 100 : 0,
            IsActive = false,
            IsHistory = true,
            IsIndeterminate = isPendingVerification
                && verificationPhase == MutationVerificationPhase.VerificationPending,
            ShowProgress = isPendingVerification
                && verificationPhase == MutationVerificationPhase.VerificationPending,
            CanCancel = false,
            ShowCancel = false,
            CanViewDiagnostic = diagnostic is not null,
            IsLiveDiagnostic = false,
            IsVerificationPending = hasUnresolvedVerification,
            VerificationPhase = hasUnresolvedVerification ? verificationPhase : null,
            DiagnosticProviderLabel = providerLabel,
            DiagnosticAutomationName = diagnostic is null
                ? string.Empty
                : $"View {providerLabel} diagnostics for {packageId}",
            DiagnosticToolTip = diagnostic is null
                ? string.Empty
                : $"View {providerLabel} diagnostics"
        };
    }

    public static OperationListItem FromVerificationMarker(MutationVerificationMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        var packageId = marker.Package.Key.Id;
        var isActivelyVerifying = marker.Phase == MutationVerificationPhase.VerificationPending;
        return new OperationListItem
        {
            OperationId = marker.OperationId,
            PackageName = string.IsNullOrWhiteSpace(marker.Package.Name)
                ? packageId
                : marker.Package.Name,
            PackageId = packageId,
            Action = marker.Kind.ToString(),
            Status = marker.Phase switch
            {
                MutationVerificationPhase.ApplicationRestartPending => "App restart needed",
                MutationVerificationPhase.RestartRequired => "Restart required",
                _ => "Verifying"
            },
            Detail = marker.Phase switch
            {
                MutationVerificationPhase.ApplicationRestartPending =>
                    "WinGet reported completion, but Windows still reports the previous version. If the app was open, close and reopen it, then check again.",
                MutationVerificationPhase.RestartRequired =>
                    "Restart Windows to complete this operation, then check again.",
                MutationVerificationPhase.OutcomeUnknown =>
                    "Package Pilot is preserving this operation because its final package state is not yet known.",
                _ => "WinGet finished; Package Pilot is checking the installed package state."
            },
            Timestamp = marker.RecordedAt.LocalDateTime.ToString("g"),
            Progress = 95,
            IsActive = false,
            IsHistory = true,
            IsIndeterminate = isActivelyVerifying,
            ShowProgress = isActivelyVerifying,
            CanCancel = false,
            ShowCancel = false,
            CanViewDiagnostic = false,
            IsLiveDiagnostic = false,
            IsVerificationPending = true,
            VerificationPhase = marker.Phase
        };
    }

    private static string GetProviderLabel(
        OperationResult result,
        OperationDiagnosticReference? diagnostic) => diagnostic?.Provider switch
        {
            OperationDiagnosticProvider.Winget
                when IsMicrosoftStoreSource(result.Package.SourceId) =>
                    "WinGet / Microsoft Store",
            OperationDiagnosticProvider.Winget => "WinGet",
            OperationDiagnosticProvider.WindowsDeployment => "Windows deployment",
            _ => string.Empty
        };

    private static string GetProviderLabel(
        PackageKey package,
        OperationTarget? target) => target switch
        {
            WingetTarget when IsMicrosoftStoreSource(package.SourceId) =>
                "WinGet / Microsoft Store",
            WingetTarget => "WinGet",
            MsixTarget => "Windows deployment",
            _ => string.Empty
        };

    private static bool IsMicrosoftStoreSource(string sourceId) =>
        string.Equals(sourceId, "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceId, "StoreEdgeFD", StringComparison.OrdinalIgnoreCase);
}
