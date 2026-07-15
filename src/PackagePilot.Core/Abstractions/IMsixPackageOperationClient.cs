using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>Foreground-only boundary for safe MSIX removal.</summary>
public interface IMsixPackageOperationClient
{
    Task<OperationResult> UninstallAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
