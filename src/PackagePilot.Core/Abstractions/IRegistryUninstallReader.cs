using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

public interface IRegistryUninstallReader
{
    Task<RegistryUninstallReadResult> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed record RegistryUninstallReadResult
{
    public IReadOnlyList<RegistryUninstallEntry> Entries { get; init; } =
        Array.Empty<RegistryUninstallEntry>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record RegistryUninstallEntry
{
    public string LocationId { get; init; } = string.Empty;
    public string SubKeyName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public InstallerScope Scope { get; init; } = InstallerScope.Unknown;
    public PackageArchitecture Architecture { get; init; } = PackageArchitecture.Unknown;
}
