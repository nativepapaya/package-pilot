using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;

namespace PackagePilot.Background;

/// <summary>
/// Narrows the background process to update discovery. Any future accidental attempt to add a
/// package/source mutation to this service graph fails closed.
/// </summary>
internal sealed class ReadOnlyWingetUpdateClient : IWingetClient
{
    private readonly WingetClient _inner = new();

    public Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetAvailableUpdatesAsync(cancellationToken);

    public Task<WingetCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetCapabilitiesAsync(cancellationToken);

    public Task<PackageSearchResult> SearchAsync(
        PackageQuery query,
        CancellationToken cancellationToken = default) => Denied<PackageSearchResult>();

    public Task<IReadOnlyList<PackageSourceStatus>> GetSourcesAsync(
        CancellationToken cancellationToken = default) => Denied<IReadOnlyList<PackageSourceStatus>>();

    public Task<IReadOnlyList<PackageSummary>> GetInstalledPackagesAsync(
        CancellationToken cancellationToken = default) => Denied<IReadOnlyList<PackageSummary>>();

    public Task<PackageDetails?> GetPackageDetailsAsync(
        PackageKey package,
        InstallPreferences? preferences = null,
        CancellationToken cancellationToken = default) => Denied<PackageDetails?>();

    public Task<OperationResult> InstallAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Denied<OperationResult>();

    public Task<OperationResult> UpgradeAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Denied<OperationResult>();

    public Task<OperationResult> UninstallAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => Denied<OperationResult>();

    private static Task<T> Denied<T>() => Task.FromException<T>(
        new NotSupportedException("The background host is restricted to read-only update discovery."));
}
