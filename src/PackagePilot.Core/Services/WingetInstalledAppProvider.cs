using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

public sealed class WingetInstalledAppProvider : IInstalledAppProvider
{
    public const string ProviderId = "winget";

    private readonly IWingetClient _client;
    private readonly IWingetInstalledAliasResolver? _aliasResolver;

    public WingetInstalledAppProvider(
        IWingetClient client,
        IWingetInstalledAliasResolver? aliasResolver = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _aliasResolver = aliasResolver;
    }

    public string Id => ProviderId;
    public InstalledAppProviderKind Kind => InstalledAppProviderKind.Winget;

    public async Task<InstalledAppProviderResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PackageSummary> packages;
        IReadOnlyDictionary<PackageKey, IReadOnlyList<InstalledAppAlias>> exactAliases =
            new Dictionary<PackageKey, IReadOnlyList<InstalledAppAlias>>();
        string? warning = null;

        if (_client is IWingetInstalledSnapshotReader snapshotReader)
        {
            var snapshot = await snapshotReader.GetInstalledSnapshotAsync(cancellationToken);
            packages = snapshot.Packages;
            exactAliases = snapshot.ExactAliases;
        }
        else
        {
            packages = await _client.GetInstalledPackagesAsync(cancellationToken);
            if (_aliasResolver is not null)
            {
                try
                {
                    exactAliases = await _aliasResolver.ResolveAliasesAsync(packages, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    warning = $"WinGet identifiers could not be enriched: {exception.Message}";
                }
            }
        }

        var installations = packages.Select(package =>
        {
            exactAliases.TryGetValue(package.Key, out var resolvedAliases);
            var aliases = new[]
                {
                    new InstalledAppAlias(InstalledAppAliasKind.WingetPackageId, package.Key.Id)
                }
                .Concat((resolvedAliases ?? Array.Empty<InstalledAppAlias>())
                    .Where(alias => alias.Kind is InstalledAppAliasKind.PackageFamilyName
                        or InstalledAppAliasKind.ProductCode))
                .ToArray();

            return new Installation
            {
                Id = $"winget:{package.Key.SourceId}:{package.Key.Id}",
                ProviderId = ProviderId,
                Provider = InstalledAppProviderKind.Winget,
                DisplayName = package.Name,
                Publisher = package.Publisher,
                Version = package.InstalledVersion ?? string.Empty,
                Icon = package.IconUri is { Scheme: "https" }
                    ? new AppIconReference
                    {
                        Kind = AppIconSourceKind.BoundedHttpsMetadata,
                        Uri = package.IconUri
                    }
                    : null,
                Aliases = aliases,
                WingetPackage = package.Key
            };
        }).ToArray();

        return new InstalledAppProviderResult
        {
            Installations = installations,
            Health = warning is null
                ? InventoryProviderHealth.Healthy
                : InventoryProviderHealth.Degraded,
            Message = warning
        };
    }
}
