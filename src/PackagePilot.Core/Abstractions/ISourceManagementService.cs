using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Framework-neutral boundary for WinGet package-source administration. Implementations must not
/// leak COM or WinRT types and must never silently accept source agreements.
/// </summary>
public interface ISourceManagementService
{
    Task<SourceManagementCapabilities> GetSourceManagementCapabilitiesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageSourceInfo>> GetSourceDetailsAsync(
        CancellationToken cancellationToken = default);

    Task<SourceOperationResult> RefreshSourceAsync(
        string sourceName,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SourceOperationResult> AddSourceAsync(
        AddPackageSourceRequest request,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SourceOperationResult> RemoveSourceAsync(
        string sourceName,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SourceOperationResult> ResetSourceAsync(
        ResetPackageSourceRequest request,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SourceOperationResult> SetSourceExplicitAsync(
        string sourceName,
        bool isExplicit,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
