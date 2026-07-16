namespace PackagePilot.Core.Models;

/// <summary>
/// Complete data-only request accepted by the elevated package helper. Arbitrary commands,
/// paths, arguments, environment values, source headers, and registry actions are deliberately
/// not representable.
/// </summary>
public sealed record PrivilegedPackageRequest
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPackageIdLength = 256;
    public const int MaximumSourceIdLength = 256;
    public const int MaximumLocaleLength = 64;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid RequestId { get; init; }
    public PackageOperationKind Kind { get; init; }
    public string PackageId { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public InstallerScope Scope { get; init; } = InstallerScope.Unknown;
    public PackageArchitecture Architecture { get; init; } = PackageArchitecture.Unknown;
    public string? Locale { get; init; }
    public bool AcceptSourceAgreements { get; init; }
    public string? AcceptedSourceAgreementFingerprint { get; init; }
    public bool AcceptPackageAgreements { get; init; }
    public string? AcceptedPackageAgreementFingerprint { get; init; }

    public static PrivilegedPackageRequest FromOperation(PackageOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.EffectiveTarget is not WingetTarget target)
        {
            throw new ArgumentException(
                "Only exact WinGet package operations can cross the package-admin boundary.",
                nameof(operation));
        }

        return new PrivilegedPackageRequest
        {
            RequestId = operation.Id,
            Kind = operation.Kind,
            PackageId = target.Package.Id,
            SourceId = target.Package.SourceId,
            Scope = operation.Preferences.Scope,
            Architecture = operation.Preferences.Architecture,
            Locale = operation.Preferences.Locale,
            AcceptSourceAgreements = operation.Preferences.AcceptSourceAgreements,
            AcceptedSourceAgreementFingerprint =
                operation.Preferences.AcceptedSourceAgreementFingerprint,
            AcceptPackageAgreements = operation.Preferences.AcceptPackageAgreements,
            AcceptedPackageAgreementFingerprint =
                operation.Preferences.AcceptedPackageAgreementFingerprint
        };
    }

    public PackageOperation ToOperation()
    {
        var package = new PackageKey(PackageId, SourceId);
        return new PackageOperation
        {
            Id = RequestId,
            Kind = Kind,
            Package = package,
            Target = new WingetTarget { Package = package },
            DisplayName = PackageId,
            Preferences = new InstallPreferences
            {
                Scope = Scope,
                Architecture = Architecture,
                Locale = Locale,
                AcceptSourceAgreements = AcceptSourceAgreements,
                AcceptedSourceAgreementFingerprint = AcceptedSourceAgreementFingerprint,
                AcceptPackageAgreements = AcceptPackageAgreements,
                AcceptedPackageAgreementFingerprint = AcceptedPackageAgreementFingerprint,
                AllowElevation = true
            },
            RunAsAdministrator = true
        };
    }
}

public sealed record PrivilegedPackageRequestValidationResult
{
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool IsValid => Errors.Count == 0;

    public static PrivilegedPackageRequestValidationResult Valid { get; } = new();
}

public static class PrivilegedPackageRequestValidator
{
    public static PrivilegedPackageRequestValidationResult Validate(
        PrivilegedPackageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        if (request.SchemaVersion != PrivilegedPackageRequest.CurrentSchemaVersion)
        {
            errors.Add("The privileged package request schema is not supported.");
        }

        if (request.RequestId == Guid.Empty)
        {
            errors.Add("A non-empty package-operation identifier is required.");
        }

        if (request.Kind is not PackageOperationKind.Install
            and not PackageOperationKind.Upgrade
            and not PackageOperationKind.Uninstall)
        {
            errors.Add("The requested package operation is not allowlisted for elevation.");
        }

        ValidateIdentifier(
            request.PackageId,
            PrivilegedPackageRequest.MaximumPackageIdLength,
            "package identifier",
            errors);
        ValidateIdentifier(
            request.SourceId,
            PrivilegedPackageRequest.MaximumSourceIdLength,
            "source identifier",
            errors);

        if (!Enum.IsDefined(request.Scope))
        {
            errors.Add("The requested installer scope is invalid.");
        }

        if (!Enum.IsDefined(request.Architecture))
        {
            errors.Add("The requested package architecture is invalid.");
        }

        ValidateLocale(request.Locale, errors);
        ValidateAgreementConsent(
            request.AcceptSourceAgreements,
            request.AcceptedSourceAgreementFingerprint,
            "source",
            errors);
        ValidateAgreementConsent(
            request.AcceptPackageAgreements,
            request.AcceptedPackageAgreementFingerprint,
            "package",
            errors);

        if (request.Kind == PackageOperationKind.Uninstall)
        {
            if (request.Architecture != PackageArchitecture.Unknown
                || request.Locale is not null
                || request.AcceptSourceAgreements
                || request.AcceptedSourceAgreementFingerprint is not null
                || request.AcceptPackageAgreements
                || request.AcceptedPackageAgreementFingerprint is not null)
            {
                errors.Add("Uninstall requests contain install-only fields.");
            }
        }

        return errors.Count == 0
            ? PrivilegedPackageRequestValidationResult.Valid
            : new PrivilegedPackageRequestValidationResult { Errors = errors };
    }

    private static void ValidateIdentifier(
        string? value,
        int maximumLength,
        string label,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            errors.Add($"An exact {label} is required.");
            return;
        }

        if (value.Length > maximumLength)
        {
            errors.Add($"The {label} is too long.");
        }

        if (value[0] == '-'
            || value.Any(character => char.IsControl(character)
            || !(char.IsLetterOrDigit(character)
                || character is '.' or '-' or '_')))
        {
            errors.Add($"The {label} contains unsupported characters.");
        }
    }

    private static void ValidateLocale(string? value, ICollection<string> errors)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length is 0 or > PrivilegedPackageRequest.MaximumLocaleLength
            || value[0] == '-'
            || value[^1] == '-'
            || value.Any(character => !(char.IsLetterOrDigit(character) || character == '-')))
        {
            errors.Add("The package locale is invalid.");
        }
    }

    private static void ValidateAgreementConsent(
        bool accept,
        string? fingerprint,
        string label,
        ICollection<string> errors)
    {
        if (accept && !PackageAgreementSnapshot.IsValidFingerprint(fingerprint))
        {
            errors.Add($"A valid exact {label}-agreement fingerprint is required.");
        }
        else if (!accept && fingerprint is not null)
        {
            errors.Add($"A {label}-agreement fingerprint requires explicit acceptance.");
        }
    }
}

public sealed record PrivilegedPackageResponse
{
    public int SchemaVersion { get; init; } = PrivilegedPackageRequest.CurrentSchemaVersion;
    public Guid RequestId { get; init; }
    public OperationResult Result { get; init; } = new()
    {
        State = PackageOperationState.Failed,
        Error = new WingetError
        {
            Kind = WingetErrorKind.Unknown,
            Code = "MissingElevatedResult",
            Message = "The elevated package helper did not return a result."
        }
    };
}
