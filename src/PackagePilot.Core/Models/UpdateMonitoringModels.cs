namespace PackagePilot.Core.Models;

/// <summary>User-selected cadence for opportunistic, read-only update discovery.</summary>
public enum UpdateMonitoringCadence
{
    Manual,
    Daily,
    EverySixHours
}

public enum BackgroundMonitoringState
{
    Disabled,
    Registered,
    Denied,
    Unavailable,
    Failed
}

public sealed record BackgroundMonitoringResult
{
    public BackgroundMonitoringState State { get; init; }
    public UpdateMonitoringCadence Cadence { get; init; }
    public string? Message { get; init; }

    public bool IsAvailable => State is BackgroundMonitoringState.Disabled or BackgroundMonitoringState.Registered;
}

/// <summary>
/// Combined durable view of Windows registration and the most recent opportunistic scan.
/// Desired and actual cadence are separate so rollback or policy failures are never presented
/// as though Windows accepted the requested schedule.
/// </summary>
public sealed record BackgroundMonitoringStatus
{
    public UpdateMonitoringCadence DesiredCadence { get; init; }
    public UpdateMonitoringCadence ActualCadence { get; init; }
    public BackgroundMonitoringState RegistrationState { get; init; }
    public bool RegistrationHealthy { get; init; }
    public DateTimeOffset? RegistrationAttemptedAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public string? Error { get; init; }
    public bool ForegroundFallbackRequired { get; init; }
}

/// <summary>One testable capability surface for Windows identity-bound integrations.</summary>
public sealed record WindowsIntegrationCapabilities
{
    public bool SupportsPackageRepair { get; init; }
    public bool SupportsSourceRefresh { get; init; }
    public bool SupportsSourceMutation { get; init; }
    public bool SupportsSourceExplicitEdit { get; init; }
    public BackgroundMonitoringState BackgroundMonitoringState { get; init; }
    public bool NotificationRegistrationSupported { get; init; }
    public bool NotificationRegistered { get; init; }
}
