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
