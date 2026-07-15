namespace PackagePilot.Core.Models;

/// <summary>The user-visible state of update discovery.</summary>
public enum UpdateCheckState
{
    NotChecked,
    Checking,
    Current,
    Stale,
    Failed
}

/// <summary>Why an update check was requested.</summary>
public enum UpdateCheckReason
{
    Automatic,
    Manual,
    PackageMutation
}

/// <summary>A stable, normalized identity for one available update.</summary>
public sealed record UpdateFingerprint
{
    public string SourceId { get; init; } = string.Empty;
    public string PackageId { get; init; } = string.Empty;
    public string AvailableVersion { get; init; } = string.Empty;
}

/// <summary>Source health captured alongside an update snapshot.</summary>
public sealed record UpdateSourceHealthSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public SourceHealth Health { get; init; } = SourceHealth.Unknown;
    public string? Message { get; init; }
}

/// <summary>
/// The last durable update-discovery result. Failed checks preserve the last successful rows
/// while advancing <see cref="LastAttemptAt"/> and recording <see cref="LastError"/>.
/// </summary>
public sealed record UpdateSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public string? LastError { get; init; }
    public IReadOnlyList<PackageSummary> Updates { get; init; } = Array.Empty<PackageSummary>();
    public IReadOnlyList<UpdateSourceHealthSnapshot> Sources { get; init; } = Array.Empty<UpdateSourceHealthSnapshot>();
    public IReadOnlyList<UpdateFingerprint> Fingerprints { get; init; } = Array.Empty<UpdateFingerprint>();
}

public sealed record UpdateCheckResult
{
    public UpdateSnapshot Snapshot { get; init; } = new();
    public UpdateCheckState State { get; init; } = UpdateCheckState.NotChecked;
    public bool PerformedCheck { get; init; }
}
