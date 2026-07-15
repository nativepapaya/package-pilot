using System.Security.Cryptography;
using System.Text;

namespace PackagePilot.Core.Models;

public sealed record SourceManagementCapabilities
{
    public const uint RefreshAndMutationContractVersion = 12;
    public const uint ExplicitEditContractVersion = 28;

    public bool IsAvailable { get; init; }
    public uint ContractVersion { get; init; }
    public bool IsCurrentProcessElevated { get; init; }
    public string? UnavailableReason { get; init; }

    public bool SupportsDetailedListing => IsAvailable && ContractVersion >= 1;
    public bool SupportsRefresh =>
        IsAvailable && ContractVersion >= RefreshAndMutationContractVersion;
    public bool SupportsAdd => SupportsRefresh;
    public bool SupportsRemove => SupportsRefresh;
    public bool SupportsResetOne => SupportsRefresh;
    public bool SupportsExplicitEdit =>
        IsAvailable && ContractVersion >= ExplicitEditContractVersion;
    public bool CanMutateInCurrentProcess => SupportsAdd && IsCurrentProcessElevated;
    public bool MutationsRequireElevation => SupportsAdd && !IsCurrentProcessElevated;
}

public sealed record PackageSourceInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public PackageSourceType Type { get; init; } = PackageSourceType.Unknown;
    public string TypeName { get; init; } = string.Empty;
    public string Argument { get; init; } = string.Empty;
    public DateTimeOffset? LastUpdatedAt { get; init; }
    public PackageSourceOrigin Origin { get; init; } = PackageSourceOrigin.Unknown;
    public PackageSourceTrustLevel TrustLevel { get; init; } = PackageSourceTrustLevel.None;
    public bool IsExplicit { get; init; }
    public SourceHealth Health { get; init; } = SourceHealth.Unknown;
    public string? Message { get; init; }
    public SourceAgreementSnapshot AgreementSnapshot { get; init; } =
        SourceAgreementSnapshot.Empty(string.Empty);

    /// <summary>
    /// Predefined catalogs are owned by WinGet and cannot be removed. WinGet's public COM
    /// projection reports organization-policy catalogs as User; the operation result remains the
    /// authority and will return BlockedByPolicy if an administrator attempts to change one.
    /// </summary>
    public bool IsPredefined => Origin == PackageSourceOrigin.Predefined;
}

public sealed record AddPackageSourceRequest
{
    public string Name { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public PackageSourceType Type { get; init; } = PackageSourceType.PreIndexed;

    /// <summary>Custom sources are excluded from unqualified discovery by default.</summary>
    public bool IsExplicit { get; init; } = true;

    /// <summary>Custom sources are not trusted by default.</summary>
    public PackageSourceTrustLevel TrustLevel { get; init; } = PackageSourceTrustLevel.None;
}

public sealed record ResetPackageSourceRequest
{
    public string SourceName { get; init; } = string.Empty;

    /// <summary>
    /// Must only be set after the named predefined source has been refreshed and the user has
    /// explicitly confirmed a reset. There is intentionally no reset-all representation.
    /// </summary>
    public bool IsConfirmed { get; init; }
}

public sealed record SourceRequestValidationResult
{
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool IsValid => Errors.Count == 0;

    public static SourceRequestValidationResult Valid { get; } = new();
}

public static class SourceRequestValidator
{
    private const string PreIndexedTypeName = "Microsoft.PreIndexed.Package";
    private const string RestTypeName = "Microsoft.Rest";

    public static SourceRequestValidationResult Validate(AddPackageSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add("A source name is required.");
        }

        if (!IsSupportedLocation(request.Location))
        {
            errors.Add("The source location must be an HTTPS address or an absolute UNC path.");
        }

        if (request.Type is not PackageSourceType.PreIndexed and not PackageSourceType.Rest)
        {
            errors.Add("Only Microsoft.PreIndexed.Package and Microsoft.Rest sources are supported.");
        }

        if (request.TrustLevel is not PackageSourceTrustLevel.None
            and not PackageSourceTrustLevel.Trusted)
        {
            errors.Add("The source trust level is not supported.");
        }

        return errors.Count == 0
            ? SourceRequestValidationResult.Valid
            : new SourceRequestValidationResult { Errors = errors };
    }

    public static SourceRequestValidationResult ValidateSourceName(string? sourceName) =>
        string.IsNullOrWhiteSpace(sourceName)
            ? new SourceRequestValidationResult
            {
                Errors = ["A source name is required."]
            }
            : SourceRequestValidationResult.Valid;

    public static SourceRequestValidationResult Validate(ResetPackageSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.SourceName))
        {
            errors.Add("A source name is required.");
        }

        if (!request.IsConfirmed)
        {
            errors.Add("Resetting a predefined source requires explicit confirmation.");
        }

        return errors.Count == 0
            ? SourceRequestValidationResult.Valid
            : new SourceRequestValidationResult { Errors = errors };
    }

    public static bool IsSupportedLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var location = value.Trim();
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment))
        {
            return true;
        }

        if (!location.StartsWith(@"\\", StringComparison.Ordinal)
            || location.StartsWith(@"\\?\", StringComparison.Ordinal)
            || location.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = location[2..]
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2
            && segments.All(segment => segment is not "." and not "..");
    }

    public static string ToDeploymentType(PackageSourceType type) => type switch
    {
        PackageSourceType.PreIndexed => PreIndexedTypeName,
        PackageSourceType.Rest => RestTypeName,
        _ => string.Empty
    };

    public static PackageSourceType FromDeploymentType(string? type)
    {
        var value = type?.Trim();
        if (string.Equals(value, PreIndexedTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return PackageSourceType.PreIndexed;
        }

        return string.Equals(value, RestTypeName, StringComparison.OrdinalIgnoreCase)
            ? PackageSourceType.Rest
            : PackageSourceType.Unknown;
    }
}

public sealed record SourceAgreementSnapshot
{
    public string SourceId { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public IReadOnlyList<PackageAgreement> Agreements { get; init; } =
        Array.Empty<PackageAgreement>();

    public bool HasAgreements => Agreements.Count > 0;

    public bool Matches(string? acceptedFingerprint) =>
        !string.IsNullOrWhiteSpace(acceptedFingerprint)
        && string.Equals(Fingerprint, acceptedFingerprint, StringComparison.Ordinal);

    public static SourceAgreementSnapshot Empty(string sourceId) =>
        Create(sourceId, Array.Empty<PackageAgreement>());

    public static SourceAgreementSnapshot Create(
        string sourceId,
        IEnumerable<PackageAgreement> agreements)
    {
        ArgumentNullException.ThrowIfNull(agreements);

        var materialized = agreements.ToArray();
        var canonical = new StringBuilder();
        Append(canonical, sourceId ?? string.Empty);
        canonical.Append(materialized.Length).Append(';');
        foreach (var agreement in materialized)
        {
            Append(canonical, agreement.Label);
            Append(canonical, agreement.Text);
            Append(canonical, agreement.AgreementUri?.OriginalString ?? string.Empty);
            canonical.Append(agreement.RequiresExplicitAcceptance ? '1' : '0').Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new SourceAgreementSnapshot
        {
            SourceId = sourceId?.Trim() ?? string.Empty,
            Fingerprint = Convert.ToHexString(hash),
            Agreements = materialized
        };
    }

    private static void Append(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }
}

public sealed record SourceOperationProgress
{
    public Guid OperationId { get; init; }
    public SourceOperationKind Kind { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public double? Percent { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SourceOperationResult
{
    public Guid OperationId { get; init; }
    public SourceOperationKind Kind { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public SourceOperationStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? HResult { get; init; }

    public bool IsSuccess => Status == SourceOperationStatus.Succeeded;
    public bool RequiresElevation => Status == SourceOperationStatus.AccessDenied;
}

public enum PackageSourceType
{
    Unknown,
    PreIndexed,
    Rest
}

public enum PackageSourceOrigin
{
    Unknown,
    Predefined,
    User
}

public enum PackageSourceTrustLevel
{
    None,
    Trusted
}

public enum SourceOperationKind
{
    Refresh,
    Add,
    Remove,
    Reset,
    EditExplicit
}

public enum SourceOperationStatus
{
    Succeeded,
    InvalidRequest,
    NotFound,
    NotAllowed,
    Unsupported,
    AccessDenied,
    BlockedByPolicy,
    AuthenticationRequired,
    Unavailable,
    Cancelled,
    Failed
}
