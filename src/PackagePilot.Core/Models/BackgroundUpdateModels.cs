namespace PackagePilot.Core.Models;

public enum BackgroundUpdateRunState
{
    Completed,
    Skipped,
    SkippedBusy,
    Failed,
    Cancelled
}

public enum UpdateScanExecutionState
{
    Completed,
    SkippedBusy
}

/// <summary>
/// Durable diagnostics for the opportunistic background host. This is deliberately separate
/// from <see cref="UpdateSnapshot"/>, which remains the source of truth for update rows.
/// </summary>
public sealed record BackgroundUpdateRunStatus
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public BackgroundUpdateRunState State { get; init; }
    public DateTimeOffset AttemptedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? LastSuccessfulRunAt { get; init; }
    public int UpdateCount { get; init; }
    public bool ForegroundFallbackRequired { get; init; }
    public bool NotificationRetryPending { get; init; }
    public string? Message { get; init; }
}

/// <summary>Result of one serialized, read-only update discovery pass.</summary>
public sealed record UpdateScanExecutionResult
{
    public UpdateScanExecutionState State { get; init; } = UpdateScanExecutionState.Completed;
    public UpdateCheckResult Check { get; init; } = new();
    public UpdateNotificationDecision Notification { get; init; } = new();
    public bool NotificationApplied { get; init; }
    public string? NotificationError { get; init; }
}
