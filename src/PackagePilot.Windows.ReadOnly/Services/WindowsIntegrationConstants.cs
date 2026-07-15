namespace PackagePilot.Windows.Services;

/// <summary>
/// Identity-bound constants shared by the foreground and restricted background hosts.
/// This type intentionally lives in the read-only Windows infrastructure assembly.
/// </summary>
public static class WindowsIntegrationConstants
{
    public const string MainInstanceKey = "PackagePilot.Main";
    public const string StartupTaskId = "PackagePilot.Startup";
    public const string BackgroundTaskName = "Package Pilot update discovery";
    public const string NotificationTag = "available-updates";
    public const string NotificationGroup = "package-pilot";

    // Identity-bound registrations. Changing either value requires a deliberate package
    // migration and corresponding manifest update.
    public static readonly Guid BackgroundTaskClassId = new("5C2B2D42-64E7-47DC-B966-1E408555A39B");
    public static readonly Guid NotificationActivatorClassId = new("B298CA11-E900-4713-AF52-0A9B104A01DA");
    public static readonly Guid NotificationAreaIconId = new("AA7F3952-947C-4577-A1B7-DEA47398CCBC");
}
