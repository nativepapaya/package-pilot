namespace PackagePilot.Core.Models;

/// <summary>
/// User-controlled process and window behavior. Startup registration itself is deliberately
/// excluded because Windows remains the source of truth for that permission.
/// </summary>
public sealed record AppBehaviorSettings
{
    public bool ShowNotificationAreaIcon { get; init; }
    public bool HideOnStartupTaskActivation { get; init; }
    public bool MinimizeToNotificationArea { get; init; }
    public bool CloseToNotificationArea { get; init; }

    public bool UsesNotificationAreaIcon =>
        ShowNotificationAreaIcon ||
        HideOnStartupTaskActivation ||
        MinimizeToNotificationArea ||
        CloseToNotificationArea;
}

public enum StartupRegistrationState
{
    Disabled,
    Enabled,
    DisabledByUser,
    DisabledByPolicy,
    EnabledByPolicy,
    Unavailable,
    Failed
}

/// <summary>The current Windows-owned state of Package Pilot's login startup task.</summary>
public sealed record StartupRegistrationResult
{
    public StartupRegistrationState State { get; init; }
    public string? Message { get; init; }

    public bool IsEnabled => State is
        StartupRegistrationState.Enabled or
        StartupRegistrationState.EnabledByPolicy;

    public bool CanEnable => State == StartupRegistrationState.Disabled;
    public bool CanDisable => State == StartupRegistrationState.Enabled;
    public bool RequiresWindowsSettings => State == StartupRegistrationState.DisabledByUser;
    public bool IsPolicyManaged => State is
        StartupRegistrationState.DisabledByPolicy or
        StartupRegistrationState.EnabledByPolicy;
}

public enum NotificationAreaAction
{
    Open,
    ReviewUpdates,
    CheckNow,
    OpenSettings,
    Exit
}

public sealed class NotificationAreaActionRequestedEventArgs : EventArgs
{
    public NotificationAreaActionRequestedEventArgs(NotificationAreaAction action)
    {
        Action = action;
    }

    public NotificationAreaAction Action { get; }
}

public enum NotificationAreaAvailabilityReason
{
    ExplorerRestartRecovered,
    ExplorerRestartFailed,
    UpdateFailed
}

public sealed class NotificationAreaAvailabilityChangedEventArgs : EventArgs
{
    public NotificationAreaAvailabilityChangedEventArgs(
        NotificationAreaIconResult result,
        NotificationAreaAvailabilityReason reason)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Reason = reason;
    }

    public NotificationAreaIconResult Result { get; }
    public NotificationAreaAvailabilityReason Reason { get; }
}

public sealed record NotificationAreaIconOptions
{
    public string ToolTip { get; init; } = "Package Pilot";
    public int? UpdateCount { get; init; }
}

public enum NotificationAreaIconState
{
    Hidden,
    Visible,
    Unavailable,
    Failed
}

public sealed record NotificationAreaIconResult
{
    public NotificationAreaIconState State { get; init; }
    public string? Message { get; init; }
    public int? NativeErrorCode { get; init; }

    public bool IsVisible => State == NotificationAreaIconState.Visible;
}

/// <summary>A UI-framework-neutral view of whether the app can suppress foreground alerts.</summary>
public readonly record struct WindowActivityState(bool IsVisible, bool IsActive)
{
    public bool IsForegroundActive => IsVisible && IsActive;
}

public sealed class WindowActivityChangedEventArgs : EventArgs
{
    public WindowActivityChangedEventArgs(WindowActivityState state)
    {
        State = state;
    }

    public WindowActivityState State { get; }
}
