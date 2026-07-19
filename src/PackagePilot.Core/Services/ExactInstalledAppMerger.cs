using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Joins installations only when providers report the same exact package-family name or product
/// code. Human-facing names, publishers, and versions are deliberately never merge keys.
/// </summary>
public sealed class ExactInstalledAppMerger : IInstalledAppMerger
{
    private static readonly Uri StoreUpdatesUri = new("ms-windows-store://downloadsandupdates");
    private static readonly Uri InstalledAppsUri = new("ms-settings:appsfeatures");

    public IReadOnlyList<InstalledApp> Merge(IEnumerable<Installation> installations)
    {
        ArgumentNullException.ThrowIfNull(installations);

        var items = installations
            .Where(installation => !string.IsNullOrWhiteSpace(installation.Id))
            .OrderBy(ProviderRank)
            .ThenBy(installation => installation.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (items.Length == 0)
        {
            return Array.Empty<InstalledApp>();
        }

        var sets = new DisjointSet(items.Length);
        var owners = new Dictionary<AliasKey, int>();

        for (var index = 0; index < items.Length; index++)
        {
            foreach (var alias in items[index].Aliases)
            {
                if (!TryCreateMergeKey(alias, out var key))
                {
                    continue;
                }

                if (owners.TryGetValue(key, out var owner))
                {
                    sets.Union(index, owner);
                }
                else
                {
                    owners.Add(key, index);
                }
            }
        }

        return items
            .Select((installation, index) => (installation, root: sets.Find(index)))
            .GroupBy(item => item.root)
            .Select(group => CreateApp(group.Select(item => item.installation)))
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static InstalledApp CreateApp(IEnumerable<Installation> candidates)
    {
        var installations = candidates
            .OrderBy(ProviderRank)
            .ThenBy(installation => installation.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var preferred = installations[0];
        var name = installations
            .Select(installation => installation.DisplayName)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? preferred.Id;
        var publisher = installations
            .Select(installation => installation.Publisher)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

        return new InstalledApp
        {
            Id = preferred.Id,
            Name = name,
            Publisher = publisher,
            // Prefer an installed resource over remote catalog artwork. Local package assets
            // are current for the exact installation, render without network work, and remain
            // available when the catalog source is offline.
            Icon = installations.Select(installation => installation.Icon)
                .Where(icon => icon is not null)
                .OrderBy(icon => IconRank(icon!.Kind))
                .FirstOrDefault(),
            Installations = installations,
            Aliases = NormalizeAliases(installations.SelectMany(installation => installation.Aliases)),
            Actions = CreateActions(installations)
        };
    }

    private static IReadOnlyList<InstalledAppAlias> NormalizeAliases(
        IEnumerable<InstalledAppAlias> aliases)
    {
        var results = new List<InstalledAppAlias>();
        var seen = new HashSet<AliasKey>();

        foreach (var alias in aliases)
        {
            var normalized = NormalizeAlias(alias);
            if (normalized is null)
            {
                continue;
            }

            var key = new AliasKey(normalized.Kind, normalized.Value.ToUpperInvariant());
            if (seen.Add(key))
            {
                results.Add(normalized);
            }
        }

        return results
            .OrderBy(alias => alias.Kind)
            .ThenBy(alias => alias.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<InstalledAppActionDescriptor> CreateActions(
        IReadOnlyList<Installation> installations)
    {
        var winget = installations.FirstOrDefault(installation =>
            installation.Provider == InstalledAppProviderKind.Winget
            && installation.WingetPackage is not null);
        var removableMsix = installations.FirstOrDefault(installation =>
            installation.IsDirectRemovalAllowed);

        // WinGet's installed catalog exposes unmatched MSIX packages through
        // version-bound synthetic IDs such as MSIX\Package_1.2.3.4_.... Those
        // identifiers can become stale as soon as the package is updated and
        // are not safe mutation targets. Never send one back through WinGet;
        // the exact PFN join below may instead provide the current, removable
        // MSIX registration or an honest Store/Windows handoff.
        if (winget is not null && !IsSyntheticMsixPackage(winget.WingetPackage))
        {
            return
            [
                new InstalledAppActionDescriptor
                {
                    Kind = InstalledAppActionKind.UninstallWithWinget,
                    Label = "Uninstall",
                    IsPrimary = true,
                    RequiresConfirmation = true,
                    CanCancel = true,
                    WingetPackage = winget.WingetPackage
                }
            ];
        }

        var actions = new List<InstalledAppActionDescriptor>();
        if (removableMsix is not null)
        {
            actions.Add(new InstalledAppActionDescriptor
            {
                Kind = InstalledAppActionKind.RemoveMsix,
                Label = "Uninstall",
                IsPrimary = true,
                RequiresConfirmation = true,
                CanCancel = false,
                PackageFullName = removableMsix.PackageFullName
            });
        }

        if (installations.Any(installation =>
                installation.Provider == InstalledAppProviderKind.Msix && installation.IsStoreApp))
        {
            actions.Add(new InstalledAppActionDescriptor
            {
                Kind = InstalledAppActionKind.OpenStoreUpdates,
                Label = "Open Store updates",
                IsPrimary = actions.Count == 0,
                Destination = StoreUpdatesUri
            });
        }

        if (actions.Count == 0
            && installations.Any(installation => installation.Provider == InstalledAppProviderKind.Registry))
        {
            actions.Add(new InstalledAppActionDescriptor
            {
                Kind = InstalledAppActionKind.OpenInstalledApps,
                Label = "Open Installed apps",
                IsPrimary = true,
                Destination = InstalledAppsUri
            });
        }

        return actions;
    }

    private static bool IsSyntheticMsixPackage(PackageKey? package) =>
        package is { Id: { } id }
        && id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase);

    private static bool TryCreateMergeKey(InstalledAppAlias alias, out AliasKey key)
    {
        key = default;
        if (alias.Kind is not (InstalledAppAliasKind.PackageFamilyName
            or InstalledAppAliasKind.ProductCode))
        {
            return false;
        }

        var normalized = NormalizeAlias(alias);
        if (normalized is null)
        {
            return false;
        }

        key = new AliasKey(normalized.Kind, normalized.Value.ToUpperInvariant());
        return true;
    }

    private static InstalledAppAlias? NormalizeAlias(InstalledAppAlias alias)
    {
        var value = alias.Value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (alias.Kind == InstalledAppAliasKind.ProductCode)
        {
            if (Guid.TryParse(value, out var productCode))
            {
                value = productCode.ToString("B").ToUpperInvariant();
            }
        }

        return new InstalledAppAlias(alias.Kind, value);
    }

    private static int ProviderRank(Installation installation) => installation.Provider switch
    {
        InstalledAppProviderKind.Winget => 0,
        InstalledAppProviderKind.Msix => 1,
        InstalledAppProviderKind.Registry => 2,
        _ => 3
    };

    private static int IconRank(AppIconSourceKind kind) => kind switch
    {
        AppIconSourceKind.MsixPackageAsset => 0,
        AppIconSourceKind.ValidatedLocalResource => 1,
        AppIconSourceKind.ValidatedExecutableResource => 2,
        AppIconSourceKind.BoundedHttpsMetadata => 3,
        _ => 4
    };

    private readonly record struct AliasKey(InstalledAppAliasKind Kind, string Value);

    private sealed class DisjointSet(int count)
    {
        private readonly int[] _parents = Enumerable.Range(0, count).ToArray();
        private readonly byte[] _ranks = new byte[count];

        public int Find(int item)
        {
            while (_parents[item] != item)
            {
                _parents[item] = _parents[_parents[item]];
                item = _parents[item];
            }

            return item;
        }

        public void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
            {
                return;
            }

            if (_ranks[leftRoot] < _ranks[rightRoot])
            {
                _parents[leftRoot] = rightRoot;
            }
            else if (_ranks[leftRoot] > _ranks[rightRoot])
            {
                _parents[rightRoot] = leftRoot;
            }
            else
            {
                _parents[rightRoot] = leftRoot;
                _ranks[leftRoot]++;
            }
        }
    }
}
