using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

public sealed class RegistryInstalledAppProvider : IInstalledAppProvider
{
    public const string ProviderId = "registry";

    private readonly IRegistryUninstallReader _reader;

    public RegistryInstalledAppProvider(IRegistryUninstallReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public string Id => ProviderId;
    public InstalledAppProviderKind Kind => InstalledAppProviderKind.Registry;

    public async Task<InstalledAppProviderResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _reader.ReadAsync(cancellationToken);
        var installations = result.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.DisplayName)
                && !string.IsNullOrWhiteSpace(entry.SubKeyName))
            .Select(entry => new Installation
            {
                Id = $"registry:{entry.LocationId}:{entry.SubKeyName}",
                ProviderId = ProviderId,
                Provider = InstalledAppProviderKind.Registry,
                DisplayName = entry.DisplayName,
                Publisher = entry.Publisher,
                Version = entry.Version,
                Scope = entry.Scope,
                Architecture = entry.Architecture,
                Aliases =
                [
                    new InstalledAppAlias(InstalledAppAliasKind.ProductCode, entry.SubKeyName),
                    new InstalledAppAlias(InstalledAppAliasKind.RegistrySubKey, entry.SubKeyName)
                ],
                SupportsDirectRemoval = false
            })
            .ToArray();

        return new InstalledAppProviderResult
        {
            Installations = installations,
            Health = result.Warnings.Count == 0
                ? InventoryProviderHealth.Healthy
                : InventoryProviderHealth.Degraded,
            Message = SummarizeWarnings(result.Warnings)
        };
    }

    private static string? SummarizeWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return null;
        }

        const int visibleWarningLimit = 3;
        var message = string.Join(" ", warnings.Take(visibleWarningLimit));
        return warnings.Count <= visibleWarningLimit
            ? message
            : $"{message} ({warnings.Count - visibleWarningLimit} more)";
    }

}
