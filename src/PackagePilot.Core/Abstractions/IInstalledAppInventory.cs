using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

public interface IInstalledAppInventory
{
    Task<InstalledAppSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>An independent source of concrete installed-application records.</summary>
public interface IInstalledAppProvider
{
    string Id { get; }
    InstalledAppProviderKind Kind { get; }

    Task<InstalledAppProviderResult> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IInstalledAppMerger
{
    IReadOnlyList<InstalledApp> Merge(IEnumerable<Installation> installations);
}

/// <summary>
/// Optional WinGet metadata seam. Implementations may return only exact PFN and product-code aliases;
/// returning no aliases is safer than guessing from display data.
/// </summary>
public interface IWingetInstalledAliasResolver
{
    Task<IReadOnlyDictionary<PackageKey, IReadOnlyList<InstalledAppAlias>>> ResolveAliasesAsync(
        IReadOnlyList<PackageSummary> packages,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads source-attributed installed WinGet packages and their exact deployment aliases in one
/// composite-catalog pass.
/// </summary>
public interface IWingetInstalledSnapshotReader
{
    Task<WingetInstalledPackageSnapshot> GetInstalledSnapshotAsync(
        CancellationToken cancellationToken = default);
}

public interface IMsixPackageReader
{
    Task<IReadOnlyList<MsixPackageRecord>> ReadCurrentUserPackagesAsync(
        CancellationToken cancellationToken = default);
}

public sealed record MsixPackageRecord
{
    public string PackageFullName { get; init; } = string.Empty;
    public string PackageFamilyName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public AppIconReference? Icon { get; init; }
    public PackageArchitecture Architecture { get; init; } = PackageArchitecture.Unknown;
    public bool IsStoreApp { get; init; }
    public bool IsSystem { get; init; }
    public bool IsFramework { get; init; }
    public bool IsResourcePackage { get; init; }
    public bool IsOptionalPackage { get; init; }
    public bool IsCurrentApp { get; init; }
}
