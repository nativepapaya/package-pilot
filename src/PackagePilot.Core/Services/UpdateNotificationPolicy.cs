using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Produces badge and notification decisions without invoking Windows APIs, keeping
/// foreground suppression and deduplication deterministic and testable.
/// </summary>
public sealed class UpdateNotificationPolicy
{
    public UpdateNotificationDecision Evaluate(
        IEnumerable<UpdateFingerprint>? previous,
        IEnumerable<UpdateFingerprint>? current,
        bool isForegroundWindowActive,
        bool checkSucceeded = true)
    {
        var previousSet = ToSet(previous);
        var currentSet = ToSet(current);

        if (!checkSucceeded)
        {
            return new UpdateNotificationDecision { BadgeCount = previousSet.Count };
        }

        var additions = currentSet
            .Except(previousSet, FingerprintComparer.Instance)
            .OrderBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.AvailableVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new UpdateNotificationDecision
        {
            BadgeCount = currentSet.Count,
            ClearNotification = currentSet.Count == 0,
            ShowOrReplaceNotification = additions.Length > 0 && !isForegroundWindowActive,
            AddedUpdates = additions
        };
    }

    private static HashSet<UpdateFingerprint> ToSet(IEnumerable<UpdateFingerprint>? values) =>
        values is null
            ? new HashSet<UpdateFingerprint>(FingerprintComparer.Instance)
            : new HashSet<UpdateFingerprint>(values, FingerprintComparer.Instance);

    private sealed class FingerprintComparer : IEqualityComparer<UpdateFingerprint>
    {
        public static FingerprintComparer Instance { get; } = new();

        public bool Equals(UpdateFingerprint? x, UpdateFingerprint? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             string.Equals(x.SourceId, y.SourceId, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(x.PackageId, y.PackageId, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(x.AvailableVersion, y.AvailableVersion, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(UpdateFingerprint value)
        {
            var hash = new HashCode();
            hash.Add(value.SourceId, StringComparer.OrdinalIgnoreCase);
            hash.Add(value.PackageId, StringComparer.OrdinalIgnoreCase);
            hash.Add(value.AvailableVersion, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
