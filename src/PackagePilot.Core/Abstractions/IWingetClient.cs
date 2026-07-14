using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Framework-neutral boundary around Microsoft.Management.Deployment. Implementations must not
/// leak COM or WinRT types through this interface.
/// </summary>
public interface IWingetClient
{
    Task<WingetCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<PackageSearchResult> SearchAsync(
        PackageQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageSourceStatus>> GetSourcesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageSummary>> GetInstalledPackagesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(
        CancellationToken cancellationToken = default);

    Task<PackageDetails?> GetPackageDetailsAsync(
        PackageKey package,
        InstallPreferences? preferences = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> InstallAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> UpgradeAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> UninstallAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
