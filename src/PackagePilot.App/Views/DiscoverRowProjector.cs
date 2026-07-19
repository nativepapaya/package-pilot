using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.App.Views;

/// <summary>
/// Projects authoritative installed, update, and queue state onto Discover results. Matching
/// is limited to exact WinGet identifiers. A source-less local-catalog identifier is used only
/// when the current result set contains exactly one source for that package ID.
/// </summary>
internal static class DiscoverRowProjector
{
    private const string AvailableGlyph = "\uE710";
    private const string InstalledGlyph = "\uE73E";
    private const string UpdateGlyph = "\uE896";
    private const string QueuedGlyph = "\uE823";
    private const string RunningGlyph = "\uE895";
    private const string UnavailableGlyph = "\uE711";

    public static DiscoverPackageStateIndex CreateIndex(
        IEnumerable<PackageSummary> searchResults,
        IEnumerable<PackageSummary> installedPackages,
        IEnumerable<PackageSummary> availableUpdates,
        OperationQueueSnapshot queue,
        IEnumerable<InstalledApp>? installedApps = null)
    {
        ArgumentNullException.ThrowIfNull(searchResults);
        ArgumentNullException.ThrowIfNull(installedPackages);
        ArgumentNullException.ThrowIfNull(availableUpdates);
        ArgumentNullException.ThrowIfNull(queue);

        return DiscoverPackageStateIndex.Create(
            searchResults,
            installedPackages,
            availableUpdates,
            queue,
            installedApps ?? Array.Empty<InstalledApp>());
    }

    public static PackageListItem Apply(
        PackageListItem item,
        PackageSummary searchResult,
        DiscoverPackageStateIndex index,
        bool mutationActionsAvailable = true,
        bool mutationVerificationPending = false,
        bool restartRequiredThisBoot = false,
        MutationVerificationPhase? mutationVerificationPhase = null,
        bool administratorRetryAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(searchResult);
        ArgumentNullException.ThrowIfNull(index);
        item.RequiresAdministratorRetry = false;

        var update = index.FindUpdate(searchResult.Key);
        var installed = index.FindInstalled(searchResult.Key);
        var installedAppId = index.FindInstalledAppId(searchResult.Key);
        var hasUpdate = searchResult.Status == PackageStatus.UpdateAvailable || update is not null;
        var isInstalled = hasUpdate
            || searchResult.Status == PackageStatus.Installed
            || !string.IsNullOrWhiteSpace(searchResult.InstalledVersion)
            || installed is not null;

        if (isInstalled)
        {
            item.InstalledVersion = FirstNonEmpty(
                update?.InstalledVersion,
                searchResult.InstalledVersion,
                installed?.InstalledVersion);
            item.AvailableVersion = hasUpdate
                ? FirstNonEmpty(update?.AvailableVersion, searchResult.AvailableVersion)
                : string.Empty;
        }

        if (hasUpdate)
        {
            SetBaseline(
                item,
                status: "Update available",
                action: "Update",
                isEnabled: mutationActionsAvailable,
                requestedKind: PackageOperationKind.Upgrade,
                glyph: UpdateGlyph,
                isPositive: false);
        }
        else if (isInstalled)
        {
            item.InstalledAppId = installedAppId;
            SetBaseline(
                item,
                status: "Installed",
                action: installedAppId is null ? "Installed" : "View installed",
                isEnabled: installedAppId is not null,
                requestedKind: null,
                glyph: InstalledGlyph,
                isPositive: true);
        }
        else if (searchResult.Status is PackageStatus.Unavailable or PackageStatus.Unmatched)
        {
            SetBaseline(
                item,
                status: searchResult.Status == PackageStatus.Unmatched
                    ? "Not managed by this source"
                    : "Unavailable",
                action: "Unavailable",
                isEnabled: false,
                requestedKind: null,
                glyph: UnavailableGlyph,
                isPositive: false);
        }
        else
        {
            SetBaseline(
                item,
                status: "Available",
                action: "Install",
                isEnabled: mutationActionsAvailable,
                requestedKind: PackageOperationKind.Install,
                glyph: AvailableGlyph,
                isPositive: false);
        }

        var active = index.FindActive(searchResult.Key);
        if (active is not null)
        {
            ApplyActiveState(
                item,
                active.Operation.Kind,
                active.Progress.State,
                active.Operation.RunAsAdministrator);
            return item;
        }

        if (restartRequiredThisBoot)
        {
            item.OperationState = PackageOperationState.RebootRequired;
            item.VerificationPhase = mutationVerificationPhase;
            item.Status = "Restart Windows to confirm the package state";
            item.ActionLabel = "Restart required";
            item.IsActionEnabled = false;
            item.StateGlyph = RunningGlyph;
            item.IsPositiveState = false;
            return item;
        }

        if (mutationVerificationPending)
        {
            item.OperationState = PackageOperationState.Completed;
            item.VerificationPhase = mutationVerificationPhase;
            item.Status = mutationVerificationPhase switch
            {
                MutationVerificationPhase.ApplicationRestartPending =>
                    "Completion unverified - close and reopen the app, then check again",
                MutationVerificationPhase.VerificationPending => "Checking the installed package state...",
                MutationVerificationPhase.RestartRequired => "Restart detected - checking package state...",
                _ => "Confirming the package operation result..."
            };
            item.ActionLabel = mutationVerificationPhase ==
                MutationVerificationPhase.ApplicationRestartPending
                    ? "App restart needed"
                    : "Verifying";
            item.IsActionEnabled = false;
            item.StateGlyph = RunningGlyph;
            item.IsPositiveState = false;
            return item;
        }

        var administratorRequired = index.FindAdministratorRequired(searchResult.Key);
        if (administratorRequired is not null
            && item.RequestedOperationKind == administratorRequired.Kind)
        {
            item.OperationState = PackageOperationState.Failed;
            item.OperationErrorKind = WingetErrorKind.AdministratorRequired;
            item.RequiresAdministratorRetry = true;
            item.Status = administratorRetryAvailable
                ? "Administrator approval required - elevated retry available"
                : "Administrator required - see Activity for details";
            item.ActionLabel = administratorRetryAvailable
                ? "Retry as administrator"
                : "Admin required";
            item.IsActionEnabled = administratorRetryAvailable;
            item.StateGlyph = UnavailableGlyph;
            item.IsPositiveState = false;
        }

        return item;
    }

    private static void SetBaseline(
        PackageListItem item,
        string status,
        string action,
        bool isEnabled,
        PackageOperationKind? requestedKind,
        string glyph,
        bool isPositive)
    {
        item.Status = status;
        item.ActionLabel = action;
        item.IsActionEnabled = isEnabled;
        item.RequestedOperationKind = requestedKind;
        item.OperationState = null;
        item.OperationErrorKind = null;
        item.VerificationPhase = null;
        item.RequiresAdministratorRetry = false;
        item.StateGlyph = glyph;
        item.IsPositiveState = isPositive;
    }

    private static void ApplyActiveState(
        PackageListItem item,
        PackageOperationKind kind,
        PackageOperationState state,
        bool runAsAdministrator)
    {
        item.OperationState = state;
        item.VerificationPhase = null;
        item.IsActionEnabled = false;
        item.IsPositiveState = false;
        item.StateGlyph = state == PackageOperationState.Queued ? QueuedGlyph : RunningGlyph;

        (item.Status, item.ActionLabel) = (kind, state) switch
        {
            (_, PackageOperationState.Resolving) when runAsAdministrator =>
                ("Waiting for Windows administrator approval...", "Admin approval"),
            (PackageOperationKind.Install, PackageOperationState.Queued) =>
                ("Install queued - waiting to start", "Queued"),
            (PackageOperationKind.Install, PackageOperationState.Resolving) =>
                ("Preparing installation...", "Preparing"),
            (PackageOperationKind.Install, PackageOperationState.Downloading) =>
                ("Downloading installer...", "Downloading"),
            (PackageOperationKind.Install, _) =>
                ("Installing app...", "Installing"),
            (PackageOperationKind.Upgrade, PackageOperationState.Queued) =>
                ("Update queued - waiting to start", "Queued"),
            (PackageOperationKind.Upgrade, PackageOperationState.Resolving) =>
                ("Preparing update...", "Preparing"),
            (PackageOperationKind.Upgrade, PackageOperationState.Downloading) =>
                ("Downloading update...", "Downloading"),
            (PackageOperationKind.Upgrade, _) =>
                ("Installing update...", "Updating"),
            (PackageOperationKind.Uninstall, PackageOperationState.Queued) =>
                ("Removal queued - waiting to start", "Queued"),
            (PackageOperationKind.Uninstall, _) =>
                ("Uninstalling app...", "Uninstalling"),
            _ => ("Package operation in progress...", "Busy")
        };
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? string.Empty;
}

internal sealed class DiscoverPackageStateIndex
{
    private static readonly PackageKeyComparer KeyComparer = new();
    private readonly IReadOnlyDictionary<PackageKey, PackageSummary> _installedByKey;
    private readonly IReadOnlyDictionary<string, PackageSummary> _unattributedInstalledById;
    private readonly IReadOnlyDictionary<PackageKey, PackageSummary> _updatesByKey;
    private readonly IReadOnlyDictionary<string, PackageSummary> _unattributedUpdatesById;
    private readonly IReadOnlyDictionary<PackageKey, OperationQueueEntry> _activeByKey;
    private readonly IReadOnlyDictionary<string, OperationQueueEntry> _unattributedActiveById;
    private readonly IReadOnlyDictionary<PackageKey, OperationResult> _administratorRequiredByKey;
    private readonly IReadOnlyDictionary<PackageKey, string> _installedAppIdsByKey;
    private readonly IReadOnlySet<string> _unambiguousSearchIds;

    private DiscoverPackageStateIndex(
        IReadOnlyDictionary<PackageKey, PackageSummary> installedByKey,
        IReadOnlyDictionary<string, PackageSummary> unattributedInstalledById,
        IReadOnlyDictionary<PackageKey, PackageSummary> updatesByKey,
        IReadOnlyDictionary<string, PackageSummary> unattributedUpdatesById,
        IReadOnlyDictionary<PackageKey, OperationQueueEntry> activeByKey,
        IReadOnlyDictionary<string, OperationQueueEntry> unattributedActiveById,
        IReadOnlyDictionary<PackageKey, OperationResult> administratorRequiredByKey,
        IReadOnlyDictionary<PackageKey, string> installedAppIdsByKey,
        IReadOnlySet<string> unambiguousSearchIds)
    {
        _installedByKey = installedByKey;
        _unattributedInstalledById = unattributedInstalledById;
        _updatesByKey = updatesByKey;
        _unattributedUpdatesById = unattributedUpdatesById;
        _activeByKey = activeByKey;
        _unattributedActiveById = unattributedActiveById;
        _administratorRequiredByKey = administratorRequiredByKey;
        _installedAppIdsByKey = installedAppIdsByKey;
        _unambiguousSearchIds = unambiguousSearchIds;
    }

    public PackageSummary? FindInstalled(PackageKey package) =>
        Find(package, _installedByKey, _unattributedInstalledById);

    public PackageSummary? FindUpdate(PackageKey package) =>
        Find(package, _updatesByKey, _unattributedUpdatesById);

    public OperationQueueEntry? FindActive(PackageKey package) =>
        Find(package, _activeByKey, _unattributedActiveById);

    public OperationResult? FindAdministratorRequired(PackageKey package) =>
        _administratorRequiredByKey.TryGetValue(package, out var result) ? result : null;

    public string? FindInstalledAppId(PackageKey package) =>
        _installedAppIdsByKey.TryGetValue(package, out var appId) ? appId : null;

    public static DiscoverPackageStateIndex Create(
        IEnumerable<PackageSummary> searchResults,
        IEnumerable<PackageSummary> installedPackages,
        IEnumerable<PackageSummary> availableUpdates,
        OperationQueueSnapshot queue,
        IEnumerable<InstalledApp> installedApps)
    {
        var sourceCounts = searchResults
            .Where(package => !package.Key.IsEmpty)
            .GroupBy(package => package.Key.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(package => package.Key.SourceId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                StringComparer.OrdinalIgnoreCase);
        var unambiguousSearchIds = sourceCounts
            .Where(pair => pair.Value == 1)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var (installedByKey, unattributedInstalledById) = IndexPackages(installedPackages);
        var (updatesByKey, unattributedUpdatesById) = IndexPackages(availableUpdates);
        var activeEntries = new[] { queue.Current }
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .Concat(queue.Pending);
        var (activeByKey, unattributedActiveById) = IndexOperations(activeEntries);
        var administratorRequiredByKey = IndexAdministratorRequired(queue.History);
        var installedAppIdsByKey = IndexInstalledApps(installedApps);

        return new DiscoverPackageStateIndex(
            installedByKey,
            unattributedInstalledById,
            updatesByKey,
            unattributedUpdatesById,
            activeByKey,
            unattributedActiveById,
            administratorRequiredByKey,
            installedAppIdsByKey,
            unambiguousSearchIds);
    }

    private static IReadOnlyDictionary<PackageKey, string> IndexInstalledApps(
        IEnumerable<InstalledApp> installedApps)
    {
        var results = new Dictionary<PackageKey, string>(KeyComparer);
        var ambiguous = new HashSet<PackageKey>(KeyComparer);
        foreach (var app in installedApps)
        {
            foreach (var package in app.Installations
                         .Select(installation => installation.WingetPackage)
                         .OfType<PackageKey>()
                         .Where(package => !package.IsEmpty && !IsUnattributedSource(package.SourceId)))
            {
                if (results.TryGetValue(package, out var existing)
                    && !string.Equals(existing, app.Id, StringComparison.Ordinal))
                {
                    ambiguous.Add(package);
                    results.Remove(package);
                    continue;
                }

                if (!ambiguous.Contains(package))
                {
                    results[package] = app.Id;
                }
            }
        }

        return results;
    }

    private T? Find<T>(
        PackageKey package,
        IReadOnlyDictionary<PackageKey, T> byKey,
        IReadOnlyDictionary<string, T> unattributedById)
        where T : class
    {
        if (byKey.TryGetValue(package, out var exact))
        {
            return exact;
        }

        return _unambiguousSearchIds.Contains(package.Id)
            && unattributedById.TryGetValue(package.Id, out var unattributed)
                ? unattributed
                : null;
    }

    private static (
        IReadOnlyDictionary<PackageKey, PackageSummary> ByKey,
        IReadOnlyDictionary<string, PackageSummary> UnattributedById) IndexPackages(
        IEnumerable<PackageSummary> packages)
    {
        var byKey = new Dictionary<PackageKey, PackageSummary>(KeyComparer);
        var unattributedById = new Dictionary<string, PackageSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.Where(package => !package.Key.IsEmpty))
        {
            if (IsUnattributedSource(package.Key.SourceId))
            {
                unattributedById.TryAdd(package.Key.Id, package);
            }
            else
            {
                byKey.TryAdd(package.Key, package);
            }
        }

        return (byKey, unattributedById);
    }

    private static IReadOnlyDictionary<PackageKey, OperationResult> IndexAdministratorRequired(
        IEnumerable<OperationResult> history)
    {
        var latestSeen = new HashSet<PackageKey>(KeyComparer);
        var administratorRequired = new Dictionary<PackageKey, OperationResult>(KeyComparer);
        foreach (var result in history.OrderByDescending(result => result.CompletedAt))
        {
            if (result.EffectiveTarget is not WingetTarget target
                || target.Package.IsEmpty
                || IsUnattributedSource(target.Package.SourceId)
                || !latestSeen.Add(target.Package))
            {
                continue;
            }

            if (RequiresAdministratorRetry(result))
            {
                administratorRequired.Add(target.Package, result);
            }
        }

        return administratorRequired;
    }

    private static bool RequiresAdministratorRetry(OperationResult result) =>
        result.Kind is PackageOperationKind.Install or PackageOperationKind.Upgrade
        && result.State is PackageOperationState.Failed or PackageOperationState.Cancelled
        && (result.Error?.Kind == WingetErrorKind.AdministratorRequired
            || result.AdministratorRetryRequested);

    private static (
        IReadOnlyDictionary<PackageKey, OperationQueueEntry> ByKey,
        IReadOnlyDictionary<string, OperationQueueEntry> UnattributedById) IndexOperations(
        IEnumerable<OperationQueueEntry> entries)
    {
        var byKey = new Dictionary<PackageKey, OperationQueueEntry>(KeyComparer);
        var unattributedById = new Dictionary<string, OperationQueueEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Operation.EffectiveTarget is not WingetTarget target || target.Package.IsEmpty)
            {
                continue;
            }

            if (IsUnattributedSource(target.Package.SourceId))
            {
                unattributedById.TryAdd(target.Package.Id, entry);
            }
            else
            {
                byKey.TryAdd(target.Package, entry);
            }
        }

        return (byKey, unattributedById);
    }

    private static bool IsUnattributedSource(string sourceId) =>
        string.IsNullOrWhiteSpace(sourceId)
        || string.Equals(sourceId, "installed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceId, "unknown", StringComparison.OrdinalIgnoreCase);

    private sealed class PackageKeyComparer : IEqualityComparer<PackageKey>
    {
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
