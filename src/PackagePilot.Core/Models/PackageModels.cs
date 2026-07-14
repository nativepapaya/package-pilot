namespace PackagePilot.Core.Models;

/// <summary>Uniquely identifies a package within a configured WinGet source.</summary>
public sealed record PackageKey(string Id, string SourceId)
{
    public static PackageKey Empty { get; } = new(string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Id);

    public override string ToString() => string.IsNullOrWhiteSpace(SourceId) ? Id : $"{SourceId}:{Id}";
}

public sealed record PackageQuery
{
    private int _limit = 100;

    public string SearchText { get; init; } = string.Empty;
    public string? SourceId { get; init; }
    public PackageMatchField MatchField { get; init; } = PackageMatchField.Default;

    /// <summary>
    /// Indicates that the user explicitly accepted agreements presented by the selected source.
    /// This is false unless the UI has collected consent for the retry.
    /// </summary>
    public bool AcceptSourceAgreements { get; init; }

    /// <summary>The maximum number of results. Values are always clamped to the V1 limit of 100.</summary>
    public int Limit
    {
        get => _limit;
        init => _limit = Math.Clamp(value, 1, 100);
    }
}

public sealed record PackageSummary
{
    public PackageKey Key { get; init; } = PackageKey.Empty;
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string? InstalledVersion { get; init; }
    public string? AvailableVersion { get; init; }
    public Uri? IconUri { get; init; }
    public PackageStatus Status { get; init; } = PackageStatus.Available;
    public ElevationRequirement ElevationRequirement { get; init; } = ElevationRequirement.Unknown;
}

public sealed record PackageDetails
{
    public PackageSummary Summary { get; init; } = new();
    public string Description { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
    public Uri? LicenseUri { get; init; }
    public Uri? PublisherUri { get; init; }
    public Uri? HomepageUri { get; init; }
    public Uri? SupportUri { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public Uri? ReleaseNotesUri { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Versions { get; init; } = Array.Empty<string>();
    public InstallerScope InstallerScope { get; init; } = InstallerScope.Unknown;
    public PackageArchitecture Architecture { get; init; } = PackageArchitecture.Unknown;
    public ElevationRequirement ElevationRequirement { get; init; } = ElevationRequirement.Unknown;
    public IReadOnlyList<PackageAgreement> Agreements { get; init; } = Array.Empty<PackageAgreement>();
}

public sealed record PackageAgreement
{
    public string Id { get; init; } = string.Empty;
    public AgreementKind Kind { get; init; } = AgreementKind.Package;
    public string Label { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public Uri? AgreementUri { get; init; }
    public bool RequiresExplicitAcceptance { get; init; } = true;
}

public sealed record PackageSearchResult
{
    public IReadOnlyList<PackageSummary> Packages { get; init; } = Array.Empty<PackageSummary>();
    public IReadOnlyList<PackageSourceStatus> Sources { get; init; } = Array.Empty<PackageSourceStatus>();
    public bool IsTruncated { get; init; }
}

public sealed record PackageSourceStatus
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public SourceHealth Health { get; init; } = SourceHealth.Unknown;
    public string? Message { get; init; }
    public IReadOnlyList<PackageAgreement> Agreements { get; init; } = Array.Empty<PackageAgreement>();
}

public enum PackageMatchField
{
    Default,
    Id,
    Name,
    Moniker,
    Tag,
    Command
}

public enum PackageStatus
{
    Unknown,
    Available,
    Installed,
    UpdateAvailable,
    Unavailable,
    Unmatched
}

public enum InstallerScope
{
    Unknown,
    User,
    Machine
}

public enum PackageArchitecture
{
    Unknown,
    Neutral,
    X86,
    X64,
    Arm,
    Arm64
}

public enum ElevationRequirement
{
    Unknown,
    NotRequired,
    MayRequire,
    Required
}

public enum AgreementKind
{
    Source,
    Package
}

public enum SourceHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unavailable,
    AuthenticationRequired
}
