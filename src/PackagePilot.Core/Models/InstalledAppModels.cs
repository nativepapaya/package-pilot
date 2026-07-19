namespace PackagePilot.Core.Models;

/// <summary>A provider-neutral view of the applications installed for the current user or machine.</summary>
public sealed record InstalledAppSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<InstalledApp> Apps { get; init; } = Array.Empty<InstalledApp>();
    public IReadOnlyList<InstalledAppProviderStatus> Providers { get; init; } =
        Array.Empty<InstalledAppProviderStatus>();

    public bool IsPartial => Providers.Any(provider => provider.Health != InventoryProviderHealth.Healthy);
}

public sealed record WingetInstalledPackageSnapshot
{
    public IReadOnlyList<PackageSummary> Packages { get; init; } = Array.Empty<PackageSummary>();
    public IReadOnlyDictionary<PackageKey, IReadOnlyList<InstalledAppAlias>> ExactAliases { get; init; } =
        new Dictionary<PackageKey, IReadOnlyList<InstalledAppAlias>>();
}

public sealed record InstalledApp
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public AppIconReference? Icon { get; init; }
    public IReadOnlyList<Installation> Installations { get; init; } = Array.Empty<Installation>();
    public IReadOnlyList<InstalledAppAlias> Aliases { get; init; } = Array.Empty<InstalledAppAlias>();
    public IReadOnlyList<InstalledAppActionDescriptor> Actions { get; init; } =
        Array.Empty<InstalledAppActionDescriptor>();

    public bool HasMultipleVersions => Installations
        .Select(installation => installation.Version)
        .Where(version => !string.IsNullOrWhiteSpace(version))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Skip(1)
        .Any();

    public string VersionDisplay => HasMultipleVersions
        ? "Multiple versions"
        : Installations.Select(installation => installation.Version)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version)) ?? string.Empty;

    public InstalledAppActionDescriptor? PrimaryAction => Actions.FirstOrDefault(action => action.IsPrimary);
}

/// <summary>A concrete installation reported by one inventory provider.</summary>
public sealed record Installation
{
    public string Id { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public InstalledAppProviderKind Provider { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public InstallerScope Scope { get; init; } = InstallerScope.Unknown;
    public PackageArchitecture Architecture { get; init; } = PackageArchitecture.Unknown;
    public AppIconReference? Icon { get; init; }
    public IReadOnlyList<InstalledAppAlias> Aliases { get; init; } = Array.Empty<InstalledAppAlias>();

    public PackageKey? WingetPackage { get; init; }
    public string? PackageFullName { get; init; }
    public bool IsStoreApp { get; init; }
    public bool IsSystem { get; init; }
    public bool IsFramework { get; init; }
    public bool IsResourcePackage { get; init; }
    public bool IsOptionalPackage { get; init; }
    public bool IsCurrentApp { get; init; }
    public bool SupportsDirectRemoval { get; init; }

    public bool IsDirectRemovalAllowed =>
        Provider == InstalledAppProviderKind.Msix
        && SupportsDirectRemoval
        && !IsSystem
        && !IsFramework
        && !IsResourcePackage
        && !IsOptionalPackage
        && !IsCurrentApp
        && !string.IsNullOrWhiteSpace(PackageFullName);
}

public sealed record AppIconReference
{
    public AppIconSourceKind Kind { get; init; }
    public Uri? Uri { get; init; }
    public string? ResourcePath { get; init; }
    public int? ResourceIndex { get; init; }
}

public enum AppIconSourceKind
{
    BoundedHttpsMetadata,
    MsixPackageAsset,
    ValidatedLocalResource
}

public sealed record InstalledAppAlias(InstalledAppAliasKind Kind, string Value);

public sealed record InstalledAppActionDescriptor
{
    public InstalledAppActionKind Kind { get; init; }
    public string Label { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public bool RequiresConfirmation { get; init; }
    public bool CanCancel { get; init; }
    public PackageKey? WingetPackage { get; init; }
    public string? PackageFullName { get; init; }
    public Uri? Destination { get; init; }
}

public sealed record InstalledAppProviderStatus
{
    public string ProviderId { get; init; } = string.Empty;
    public InstalledAppProviderKind Provider { get; init; }
    public InventoryProviderHealth Health { get; init; } = InventoryProviderHealth.Healthy;
    public int InstallationCount { get; init; }
    public string? Message { get; init; }
}

public sealed record InstalledAppProviderResult
{
    public IReadOnlyList<Installation> Installations { get; init; } = Array.Empty<Installation>();
    public InventoryProviderHealth Health { get; init; } = InventoryProviderHealth.Healthy;
    public string? Message { get; init; }
}

public enum InstalledAppProviderKind
{
    Winget,
    Msix,
    Registry
}

public enum InventoryProviderHealth
{
    Healthy,
    Degraded,
    Unavailable
}

public enum InstalledAppAliasKind
{
    WingetPackageId,
    PackageFamilyName,
    ProductCode,
    RegistrySubKey
}

public enum InstalledAppActionKind
{
    UninstallWithWinget,
    RemoveMsix,
    OpenStoreUpdates,
    OpenInstalledApps
}
