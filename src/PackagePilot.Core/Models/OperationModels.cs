namespace PackagePilot.Core.Models;

public sealed record InstallPreferences
{
    public InstallerScope Scope { get; init; } = InstallerScope.Unknown;
    public PackageArchitecture Architecture { get; init; } = PackageArchitecture.Unknown;
    public string? Locale { get; init; }
    public bool AcceptSourceAgreements { get; init; }
    public bool AcceptPackageAgreements { get; init; }
    public bool AllowElevation { get; init; } = true;
}

public sealed record PackageOperation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public PackageOperationKind Kind { get; init; }
    public PackageKey Package { get; init; } = PackageKey.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public InstallPreferences Preferences { get; init; } = new();
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;

    public static PackageOperation Create(
        PackageOperationKind kind,
        PackageKey package,
        string? displayName = null,
        InstallPreferences? preferences = null) =>
        new()
        {
            Kind = kind,
            Package = package,
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

    public bool CanCancel => State is PackageOperationState.Queued
        or PackageOperationState.Resolving
        or PackageOperationState.Downloading;
}

public sealed record OperationResult
{
    public Guid OperationId { get; init; }
    public PackageKey Package { get; init; } = PackageKey.Empty;
    public PackageOperationKind Kind { get; init; }
    public PackageOperationState State { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public WingetError? Error { get; init; }
    public bool RebootRequired { get; init; }

    public bool IsSuccess => State is PackageOperationState.Completed or PackageOperationState.RebootRequired;
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

    public bool IsAvailable { get; init; }
    public uint ContractVersion { get; init; }
    public string? Version { get; init; }
    public string? UnavailableReason { get; init; }

    public bool MeetsMinimumContract => IsAvailable && ContractVersion >= RequiredContractVersion;
    public bool SupportsPackageMetadata => MeetsMinimumContract;
    public bool SupportsAgreementHandling => MeetsMinimumContract;

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
    ComFailure
}
