using PackagePilot.Core.Models;

namespace PackagePilot.App.Views;

internal readonly record struct UpdateNavigationBadgeState(int Count)
{
    public bool IsVisible => Count > 0;

    public string AutomationName => Count switch
    {
        0 => "No updates available",
        1 => "1 update available",
        _ => $"{Count} updates available"
    };
}

internal static class UpdateNavigationBadgeProjector
{
    public static UpdateNavigationBadgeState Create(
        IEnumerable<PackageSummary> availableUpdates,
        IEnumerable<PackageSummary> pendingVerifications,
        OperationQueueSnapshot queue)
    {
        ArgumentNullException.ThrowIfNull(availableUpdates);
        ArgumentNullException.ThrowIfNull(pendingVerifications);
        ArgumentNullException.ThrowIfNull(queue);

        var suppressed = new HashSet<PackageKey>(
            pendingVerifications
                .Select(package => package.Key)
                .Where(package => !package.IsEmpty),
            PackageKeyComparer.Instance);

        SuppressQueuedUpgrade(queue.Current, suppressed);
        foreach (var entry in queue.Pending)
        {
            SuppressQueuedUpgrade(entry, suppressed);
        }

        var actionable = new HashSet<PackageKey>(PackageKeyComparer.Instance);
        foreach (var update in availableUpdates)
        {
            if (!update.Key.IsEmpty && !suppressed.Contains(update.Key))
            {
                actionable.Add(update.Key);
            }
        }

        return new UpdateNavigationBadgeState(actionable.Count);
    }

    private static void SuppressQueuedUpgrade(
        OperationQueueEntry? entry,
        ISet<PackageKey> suppressed)
    {
        if (entry?.Operation is
            {
                Kind: PackageOperationKind.Upgrade,
                EffectiveTarget: WingetTarget target
            })
        {
            suppressed.Add(target.Package);
        }
    }

    private sealed class PackageKeyComparer : IEqualityComparer<PackageKey>
    {
        public static PackageKeyComparer Instance { get; } = new();

        public bool Equals(PackageKey? left, PackageKey? right) =>
            ReferenceEquals(left, right)
            || (left is not null
                && right is not null
                && string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(PackageKey package) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(package.Id),
            StringComparer.OrdinalIgnoreCase.GetHashCode(package.SourceId));
    }
}
