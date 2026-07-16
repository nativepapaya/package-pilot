using System.Text.Json.Serialization;

namespace PackagePilot.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$target")]
[JsonDerivedType(typeof(WingetTarget), "winget")]
[JsonDerivedType(typeof(MsixTarget), "msix")]
public abstract record OperationTarget
{
    public abstract string Id { get; }
}

public sealed record WingetTarget : OperationTarget
{
    public PackageKey Package { get; init; } = PackageKey.Empty;
    public override string Id => Package.Id;
}

public sealed record MsixTarget : OperationTarget
{
    public string PackageFullName { get; init; } = string.Empty;
    public string PackageFamilyName { get; init; } = string.Empty;
    public override string Id => PackageFullName;
}

public sealed record InstallPreferences
{
    public InstallerScope Scope { get; init; } = InstallerScope.Unknown;
    public PackageArchitecture Architecture { get; init; } = PackageArchitecture.Unknown;
    public string? Locale { get; init; }
    public bool AcceptSourceAgreements { get; init; }
    public string? AcceptedSourceAgreementFingerprint { get; init; }
    public bool AcceptPackageAgreements { get; init; }
    public string? AcceptedPackageAgreementFingerprint { get; init; }
    public bool AllowElevation { get; init; } = true;
}

public sealed record PackageOperation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public PackageOperationKind Kind { get; init; }
    public PackageKey Package { get; init; } = PackageKey.Empty;
    public OperationTarget? Target { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public InstallPreferences Preferences { get; init; } = new();
    public bool RunAsAdministrator { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public OperationTarget? EffectiveTarget => Target ??
        (Package.IsEmpty ? null : new WingetTarget { Package = Package });

    public static PackageOperation Create(
        PackageOperationKind kind,
        PackageKey package,
        string? displayName = null,
        InstallPreferences? preferences = null) =>
        new()
        {
            Kind = kind,
            Package = package,
            Target = new WingetTarget { Package = package },
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? package.Id : displayName,
            Preferences = preferences ?? new InstallPreferences()
        };
}

public sealed record OperationProgress
{
    public Guid OperationId { get; init; }
    public PackageOperationState State { get; init; } = PackageOperationState.Queued;
    public double? Percent { get; init; }
    public string? Message { get; init; }
    public long? BytesTransferred { get; init; }
    public long? BytesTotal { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool CancellationSupported { get; init; } = true;

    public bool CanCancel => CancellationSupported && State is (
        PackageOperationState.Queued
        or PackageOperationState.Resolving
        or PackageOperationState.Downloading);
}

public sealed record OperationResult
{
    public Guid OperationId { get; init; }
    public PackageKey Package { get; init; } = PackageKey.Empty;
    public OperationTarget? Target { get; init; }
    public PackageOperationKind Kind { get; init; }
    public PackageOperationState State { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public WingetError? Error { get; init; }
    public bool RebootRequired { get; init; }
    public bool AdministratorRetryRequested { get; init; }
    public bool RanAsAdministrator { get; init; }
    public OperationDiagnosticReference? Diagnostic { get; init; }

    public bool IsSuccess => State is PackageOperationState.Completed or PackageOperationState.RebootRequired;

    [JsonIgnore]
    public OperationTarget? EffectiveTarget => Target ??
        (Package.IsEmpty ? null : new WingetTarget { Package = Package });

    [JsonIgnore]
    public OperationDiagnosticReference? EffectiveDiagnostic =>
        IsValidDiagnostic(Diagnostic)
            ? Diagnostic
            : EffectiveTarget is WingetTarget && OperationId != Guid.Empty
                ? new OperationDiagnosticReference
                {
                    Provider = OperationDiagnosticProvider.Winget,
                    ReferenceId = OperationId
                }
                : null;

    private bool IsValidDiagnostic(OperationDiagnosticReference? diagnostic) =>
        diagnostic is not null
        && diagnostic.ReferenceId != Guid.Empty
        && Enum.IsDefined(diagnostic.Provider)
        && (diagnostic.Provider switch
        {
            OperationDiagnosticProvider.Winget =>
                EffectiveTarget is WingetTarget && diagnostic.ReferenceId == OperationId,
            OperationDiagnosticProvider.WindowsDeployment => EffectiveTarget is MsixTarget,
            _ => false
        });
}

public sealed record WingetError
{
    public WingetErrorKind Kind { get; init; } = WingetErrorKind.Unknown;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int? HResult { get; init; }
}

public sealed record WingetCapabilities
{
    public const uint RequiredContractVersion = 6;
    public const uint PackageRepairContractVersion = 11;

    public bool IsAvailable { get; init; }
    public uint ContractVersion { get; init; }
    public string? Version { get; init; }
    public string? UnavailableReason { get; init; }

    public bool MeetsMinimumContract => IsAvailable && ContractVersion >= RequiredContractVersion;
    public bool SupportsPackageMetadata => MeetsMinimumContract;
    public bool SupportsAgreementHandling => MeetsMinimumContract;
    public bool SupportsPackageRepair =>
        IsAvailable && ContractVersion >= PackageRepairContractVersion;

    public static WingetCapabilities Unavailable(string reason) => new()
    {
        IsAvailable = false,
        UnavailableReason = reason
    };
}

public sealed record OperationQueueEntry(PackageOperation Operation, OperationProgress Progress);

public sealed record OperationQueueSnapshot
{
    public IReadOnlyList<OperationQueueEntry> Pending { get; init; } = Array.Empty<OperationQueueEntry>();
    public OperationQueueEntry? Current { get; init; }
    public IReadOnlyList<OperationResult> History { get; init; } = Array.Empty<OperationResult>();
    public Exception? LastPersistenceError { get; init; }

    public bool IsIdle => Pending.Count == 0 && Current is null;
}

public sealed class OperationQueueChangedEventArgs(OperationQueueSnapshot snapshot) : EventArgs
{
    public OperationQueueSnapshot Snapshot { get; } = snapshot;
}

public enum PackageOperationKind
{
    Install,
    Upgrade,
    Uninstall
}

public enum PackageOperationState
{
    Queued,
    Resolving,
    Downloading,
    Installing,
    Upgrading,
    Uninstalling,
    Completed,
    Failed,
    Cancelled,
    RebootRequired
}

public enum WingetErrorKind
{
    Unknown,
    AppInstallerMissing,
    ContractTooOld,
    PolicyBlocked,
    Authentication,
    Network,
    PackageNotFound,
    InstallerUnavailable,
    AgreementRequired,
    ElevationDenied,
    Cancelled,
    ComFailure,
    AdministratorRequired,
    OutcomeUnknown,
    ApplicationInUse,
    NoChangeDetected
}
