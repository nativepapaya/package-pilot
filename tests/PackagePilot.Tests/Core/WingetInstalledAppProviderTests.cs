using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class WingetInstalledAppProviderTests
{
    [Fact]
    public async Task ReadAsync_UsesSingleSourceAttributedSnapshotWhenAvailable()
    {
        var package = new PackageSummary
        {
            Key = new PackageKey("Contoso.App", "winget-source"),
            Name = "Contoso App",
            Publisher = "Contoso",
            InstalledVersion = "1.0.0",
            Status = PackageStatus.Installed
        };
        var client = new SnapshotWingetClient(new WingetInstalledPackageSnapshot
        {
            Packages = [package],
            ExactAliases = new Dictionary<PackageKey, IReadOnlyList<InstalledAppAlias>>
            {
                [package.Key] =
                [
                    new InstalledAppAlias(
                        InstalledAppAliasKind.ProductCode,
                        "{01234567-89AB-CDEF-0123-456789ABCDEF}")
                ]
            }
        });
        var provider = new WingetInstalledAppProvider(client, new ThrowingAliasResolver());

        var result = await provider.ReadAsync();

        var installation = Assert.Single(result.Installations);
        Assert.Equal(package.Key, installation.WingetPackage);
        Assert.Contains(
            installation.Aliases,
            alias => alias.Kind == InstalledAppAliasKind.WingetPackageId
                && alias.Value == package.Key.Id);
        Assert.Contains(
            installation.Aliases,
            alias => alias.Kind == InstalledAppAliasKind.ProductCode
                && alias.Value == "{01234567-89AB-CDEF-0123-456789ABCDEF}");
        Assert.Equal(InventoryProviderHealth.Healthy, result.Health);
        Assert.Equal(1, client.SnapshotReadCount);
        Assert.Equal(0, client.LegacyInventoryReadCount);
    }

    private sealed class SnapshotWingetClient(WingetInstalledPackageSnapshot snapshot)
        : IWingetClient, IWingetInstalledSnapshotReader
    {
        public int SnapshotReadCount { get; private set; }
        public int LegacyInventoryReadCount { get; private set; }

        public Task<WingetInstalledPackageSnapshot> GetInstalledSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotReadCount++;
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<PackageSummary>> GetInstalledPackagesAsync(
            CancellationToken cancellationToken = default)
        {
            LegacyInventoryReadCount++;
            return Task.FromResult<IReadOnlyList<PackageSummary>>(snapshot.Packages);
        }

        public Task<WingetCapabilities> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WingetCapabilities());

        public Task<PackageSearchResult> SearchAsync(
            PackageQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PackageSearchResult());

        public Task<IReadOnlyList<PackageSourceStatus>> GetSourcesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSourceStatus>>([]);

        public Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSummary>>([]);

        public Task<PackageDetails?> GetPackageDetailsAsync(
            PackageKey package,
            InstallPreferences? preferences = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PackageDetails?>(null);

        public Task<OperationResult> InstallAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationResult> UpgradeAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OperationResult> UninstallAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingAliasResolver : IWingetInstalledAliasResolver
    {
        public Task<IReadOnlyDictionary<PackageKey, IReadOnlyList<InstalledAppAlias>>>
            ResolveAliasesAsync(
                IReadOnlyList<PackageSummary> packages,
                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The snapshot reader should supply aliases.");
    }
}
