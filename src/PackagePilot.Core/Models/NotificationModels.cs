namespace PackagePilot.Core.Models;

/// <summary>Side effects requested after a successful update-discovery result.</summary>
public sealed record UpdateNotificationDecision
{
    public int BadgeCount { get; init; }
    public bool ClearNotification { get; init; }
    public bool ShowOrReplaceNotification { get; init; }
    public IReadOnlyList<UpdateFingerprint> AddedUpdates { get; init; } = Array.Empty<UpdateFingerprint>();
}
