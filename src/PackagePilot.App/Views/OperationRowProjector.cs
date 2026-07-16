using PackagePilot.Core.Models;

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

    public static OperationListItem FromResult(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var packageId = result.EffectiveTarget?.Id ?? result.Package.Id;
        var diagnostic = result.EffectiveDiagnostic;
        var providerLabel = GetProviderLabel(result, diagnostic);
        return new OperationListItem
        {
            OperationId = result.OperationId,
            PackageName = packageId,
            PackageId = packageId,
            Action = result.Kind.ToString(),
            Status = result.State.ToString(),
            Detail = result.Error?.Message
                ?? (result.RebootRequired
                    ? "Restart Windows to complete this operation."
                    : "Completed"),
            Timestamp = result.CompletedAt.LocalDateTime.ToString("g"),
            Progress = result.IsSuccess ? 100 : 0,
            IsActive = false,
            IsHistory = true,
            IsIndeterminate = false,
            ShowProgress = false,
            CanCancel = false,
            ShowCancel = false,
            CanViewDiagnostic = diagnostic is not null,
            IsLiveDiagnostic = false,
            DiagnosticProviderLabel = providerLabel,
            DiagnosticAutomationName = diagnostic is null
                ? string.Empty
                : $"View {providerLabel} diagnostics for {packageId}",
            DiagnosticToolTip = diagnostic is null
                ? string.Empty
                : $"View {providerLabel} diagnostics"
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
