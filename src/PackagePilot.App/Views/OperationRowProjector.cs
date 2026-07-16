using PackagePilot.Core.Models;

namespace PackagePilot.App.Views;

internal static class OperationRowProjector
{
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
            IsIndeterminate = false,
            CanCancel = false,
            CanViewDiagnostic = diagnostic is not null,
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

    private static bool IsMicrosoftStoreSource(string sourceId) =>
        string.Equals(sourceId, "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceId, "StoreEdgeFD", StringComparison.OrdinalIgnoreCase);
}
