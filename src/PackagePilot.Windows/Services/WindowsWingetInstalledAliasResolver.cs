using Microsoft.Management.Deployment;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using Windows.Foundation;

namespace PackagePilot.Windows.Services;

/// <summary>Reads WinGet's exact installed PFN/product-code metadata in one local-catalog pass.</summary>
public sealed class WindowsWingetInstalledAliasResolver : IWingetInstalledAliasResolver
{
    public async Task<IReadOnlyDictionary<PackageKey, IReadOnlyList<InstalledAppAlias>>>
        ResolveAliasesAsync(
            IReadOnlyList<PackageSummary> packages,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);
        cancellationToken.ThrowIfCancellationRequested();

        var manager = new Microsoft.Management.Deployment.PackageManager();
        var reference = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);
        var connectOperation = await Task.Run(reference.ConnectAsync, cancellationToken);
        var connectResult = await AwaitAsync(connectOperation, cancellationToken);
        if (connectResult.Status != ConnectResultStatus.Ok)
        {
            throw new InvalidOperationException(
                $"The installed WinGet catalog could not be opened ({connectResult.Status}).");
        }

        var findResult = await AwaitAsync(
            connectResult.PackageCatalog.FindPackagesAsync(new FindPackagesOptions()),
            cancellationToken);
        if (findResult.Status != FindPackagesResultStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Installed WinGet identifiers could not be read ({findResult.Status}).");
        }

        var aliasesById = new Dictionary<string, IReadOnlyList<InstalledAppAlias>>(
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < findResult.Matches.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var catalogPackage = findResult.Matches[index].CatalogPackage;
            var installedVersion = catalogPackage.InstalledVersion;
            if (installedVersion is null)
            {
                continue;
            }

            var aliases = aliasesById.TryGetValue(catalogPackage.Id, out var existing)
                ? existing.ToList()
                : new List<InstalledAppAlias>();
            AddAliases(
                aliases,
                installedVersion.PackageFamilyNames,
                InstalledAppAliasKind.PackageFamilyName);
            AddAliases(
                aliases,
                installedVersion.ProductCodes,
                InstalledAppAliasKind.ProductCode);
            aliasesById[catalogPackage.Id] = aliases;
        }

        var results = new Dictionary<PackageKey, IReadOnlyList<InstalledAppAlias>>();
        foreach (var package in packages)
        {
            results[package.Key] = aliasesById.TryGetValue(package.Key.Id, out var aliases)
                ? aliases
                : Array.Empty<InstalledAppAlias>();
        }

        return results;
    }

    private static void AddAliases(
        ICollection<InstalledAppAlias> target,
        IReadOnlyList<string> values,
        InstalledAppAliasKind kind)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Add(new InstalledAppAlias(kind, value));
            }
        }
    }

    private static async Task<T> AwaitAsync<T>(
        IAsyncOperation<T> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(operation.Cancel);
        return await operation;
    }
}
