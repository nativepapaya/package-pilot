using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Durable, provider-exact state for WinGet mutations that must be confirmed by a later
/// read-only update scan. Unknown outcomes and reboot requirements are tied to the Windows
/// boot session in which the operation was admitted, rather than to the wall clock.
/// </summary>
public sealed class MutationVerificationTracker
{
    public const int MaximumVerifiedOperationCount = 100;

    private readonly string? _currentBootSessionId;
    private readonly Dictionary<PackageKey, MutationVerificationMarker> _markers = [];
    private readonly HashSet<Guid> _verifiedOperationIds = [];
    private readonly Queue<Guid> _verifiedOperationOrder = [];

    public MutationVerificationTracker(string? currentBootSessionId) =>
        _currentBootSessionId = string.IsNullOrWhiteSpace(currentBootSessionId)
            ? null
            : currentBootSessionId;

    public int Count => _markers.Count;

    public bool CanAdmitMutations => _currentBootSessionId is not null;

    public bool HasTargetsEligibleForVerification =>
        _markers.Values.Any(IsEligibleForVerification);

    public bool HasUpgradeTargetsEligibleForVerification =>
        _markers.Values.Any(marker =>
            marker.Kind == PackageOperationKind.Upgrade
            && IsEligibleForVerification(marker));

    public bool Contains(PackageKey package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return _markers.ContainsKey(package);
    }

    internal MutationVerificationMarker GetMarker(PackageKey package) =>
        _markers.TryGetValue(package, out var marker)
            ? marker
            : throw new KeyNotFoundException(
                $"No mutation verification marker exists for '{package}'.");

    public bool MarkEnqueued(PackageOperation operation, PackageSummary package)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(package);
        if (_currentBootSessionId is null)
        {
            throw new MutationRecoveryUnavailableException(
                "The current Windows boot session could not be identified safely.");
        }

        if (operation.EffectiveTarget is not WingetTarget target
            || target.Package != package.Key)
        {
            throw new ArgumentException(
                "The operation and package must identify the same WinGet target.",
                nameof(operation));
        }

        var marker = new MutationVerificationMarker
        {
            OperationId = operation.Id,
            RevisionId = Guid.NewGuid(),
            Kind = operation.Kind,
            Package = CreateMarkerPackage(package),
            RecordedAt = operation.EnqueuedAt,
            BootSessionId = _currentBootSessionId,
            Phase = MutationVerificationPhase.OutcomeUnknown
        };
        if (_markers.ContainsKey(marker.Package.Key))
        {
            return false;
        }

        _markers.Add(marker.Package.Key, marker);
        return true;
    }

    public bool RecordResult(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.EffectiveTarget is not WingetTarget target
            || !_markers.TryGetValue(target.Package, out var existing)
            || existing.OperationId != result.OperationId)
        {
            return false;
        }

        if (existing.Phase == MutationVerificationPhase.RestartRequired)
        {
            return false;
        }

        if (existing.Phase == MutationVerificationPhase.VerificationPending)
        {
            if (result.State != PackageOperationState.RebootRequired)
            {
                return false;
            }

            return SetMarker(existing with
            {
                RevisionId = Guid.NewGuid(),
                RecordedAt = result.CompletedAt,
                Phase = MutationVerificationPhase.RestartRequired
            });
        }

        if (result.State is PackageOperationState.Failed or PackageOperationState.Cancelled)
        {
            // A one-shot elevated helper can lose its response after the exact mutation has
            // crossed the privilege boundary. That transport failure is not evidence that
            // nothing changed. Preserve the durable outcome-unknown marker so another
            // mutation stays blocked until the normal post-restart verification flow runs.
            if (result.Error?.Kind == WingetErrorKind.OutcomeUnknown)
            {
                return false;
            }

            var removed = _markers.Remove(target.Package);
            return AddVerifiedOperation(result.OperationId) || removed;
        }

        if (!result.IsSuccess)
        {
            return false;
        }

        return SetMarker(existing with
        {
            RevisionId = Guid.NewGuid(),
            RecordedAt = result.CompletedAt,
            Phase = result.State == PackageOperationState.RebootRequired
                ? MutationVerificationPhase.RestartRequired
                : MutationVerificationPhase.VerificationPending
        });
    }

    public bool ReconcileHistory(
        IEnumerable<OperationResult> history,
        IEnumerable<PackageSummary> cachedUpdates)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(cachedUpdates);

        var changed = false;
        var cachedByKey = cachedUpdates
            .GroupBy(package => package.Key)
            .ToDictionary(group => group.Key, group => group.First());
        var resultsByPackage = history
            .Where(result => result.EffectiveTarget is WingetTarget)
            .GroupBy(result => ((WingetTarget)result.EffectiveTarget!).Package);

        foreach (var packageResults in resultsByPackage)
        {
            if (_markers.TryGetValue(packageResults.Key, out var existing))
            {
                var matchingResult = packageResults
                    .Where(result => result.OperationId == existing.OperationId)
                    .OrderByDescending(result => result.CompletedAt)
                    .FirstOrDefault();
                if (matchingResult is not null)
                {
                    changed |= RecordResult(matchingResult);
                }

                // A different operation for the same exact package must never transition,
                // remove, or replace a durable marker.
                continue;
            }

            var resultToRecover = packageResults
                .Where(result => result.IsSuccess
                    && !_verifiedOperationIds.Contains(result.OperationId))
                .OrderByDescending(result => result.CompletedAt)
                .FirstOrDefault();
            if (resultToRecover is null || _currentBootSessionId is null)
            {
                continue;
            }

            cachedByKey.TryGetValue(packageResults.Key, out var cachedPackage);
            changed |= SetMarker(new MutationVerificationMarker
            {
                OperationId = resultToRecover.OperationId,
                RevisionId = Guid.NewGuid(),
                Kind = resultToRecover.Kind,
                Package = cachedPackage is null
                    ? CreateFallbackPackage(packageResults.Key)
                    : CreateMarkerPackage(cachedPackage),
                RecordedAt = resultToRecover.CompletedAt,
                BootSessionId = _currentBootSessionId,
                // Without the original marker, the terminal result's boot session is
                // unknowable. Require a later Windows boot before trusting a scan.
                Phase = MutationVerificationPhase.OutcomeUnknown
            });
        }

        return changed;
    }

    public bool Import(MutationVerificationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        if (_markers.Count > 0 || _verifiedOperationIds.Count > 0)
        {
            throw new InvalidOperationException(
                "A mutation verification snapshot can only be imported into an empty tracker.");
        }

        var changed = false;
        foreach (var operationId in snapshot.VerifiedOperationIds)
        {
            changed |= AddVerifiedOperation(operationId);
        }

        foreach (var candidate in snapshot.Markers)
        {
            var marker = candidate with
            {
                Package = CreateMarkerPackage(candidate.Package)
            };
            _markers.Add(marker.Package.Key, marker);
            changed = true;
        }

        return changed;
    }

    public MutationVerificationSnapshot CreateSnapshot() => new()
    {
        Markers = Export(),
        VerifiedOperationIds = _verifiedOperationOrder.ToArray()
    };

    public IReadOnlyList<MutationVerificationMarker> Export() =>
        _markers.Values
            .OrderBy(marker => marker.Package.Key.SourceId, StringComparer.Ordinal)
            .ThenBy(marker => marker.Package.Key.Id, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<PackageSummary> GetPendingUpgradeVerifications() =>
        _markers.Values
            .Where(marker => marker.Kind == PackageOperationKind.Upgrade)
            .Select(marker => marker.Package)
            .ToArray();

    public IReadOnlyList<PackageSummary> GetRestartRequiredUpdatesForCurrentBoot() =>
        _markers.Values
            .Where(marker => marker.Kind == PackageOperationKind.Upgrade
                && IsRestartRequiredThisBoot(marker))
            .Select(marker => marker.Package)
            .ToArray();

    public MutationVerificationTarget[] CaptureVerificationTargetsForCurrentBoot() =>
        _markers.Values
            .Where(IsEligibleForVerification)
            .Select(marker => new MutationVerificationTarget(
                marker.Package.Key,
                marker.OperationId,
                marker.RevisionId,
                marker.Kind))
            .ToArray();

    public MutationVerificationTarget[] CaptureVerificationTargetsForCurrentBoot(
        PackageOperationKind kind) =>
        _markers.Values
            .Where(marker => marker.Kind == kind && IsEligibleForVerification(marker))
            .Select(marker => new MutationVerificationTarget(
                marker.Package.Key,
                marker.OperationId,
                marker.RevisionId,
                marker.Kind))
            .ToArray();

    public bool IsRestartRequiredThisBoot(PackageKey package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return _markers.TryGetValue(package, out var marker)
            && IsRestartRequiredThisBoot(marker);
    }

    public MutationVerificationPhase? GetPhase(PackageKey package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return _markers.TryGetValue(package, out var marker)
            ? marker.Phase
            : null;
    }

    public UpdateCheckReason GetEffectiveCheckReason(UpdateCheckReason requestedReason) =>
        requestedReason == UpdateCheckReason.Manual && HasUpgradeTargetsEligibleForVerification
            ? UpdateCheckReason.PackageMutation
            : requestedReason;

    public bool CompleteVerification(IEnumerable<MutationVerificationTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var changed = false;
        foreach (var target in targets)
        {
            if (!_markers.TryGetValue(target.Package, out var marker)
                || marker.OperationId != target.OperationId
                || marker.RevisionId != target.RevisionId
                || !IsEligibleForVerification(marker))
            {
                continue;
            }

            _markers.Remove(target.Package);
            changed = true;
            changed |= AddVerifiedOperation(marker.OperationId);
        }

        return changed;
    }

    public bool RemoveMarkers(IEnumerable<MutationVerificationMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var changed = false;
        foreach (var marker in markers)
        {
            if (_markers.TryGetValue(marker.Package.Key, out var current)
                && current.OperationId == marker.OperationId
                && current.RevisionId == marker.RevisionId)
            {
                changed |= _markers.Remove(marker.Package.Key);
            }
        }

        return changed;
    }

    public bool RestoreMarkers(IEnumerable<MutationVerificationMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var changed = false;
        foreach (var marker in markers)
        {
            if (!_markers.ContainsKey(marker.Package.Key))
            {
                _markers.Add(marker.Package.Key, marker);
                changed = true;
            }
        }

        return changed;
    }

    public bool SeedVerifiedOperations(IEnumerable<OperationResult> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var changed = false;
        foreach (var result in history.OrderBy(result => result.CompletedAt))
        {
            changed |= AddVerifiedOperation(result.OperationId);
        }

        return changed;
    }

    private bool IsEligibleForVerification(MutationVerificationMarker marker) =>
        marker.Phase == MutationVerificationPhase.VerificationPending
        || (_currentBootSessionId is not null
            && !string.Equals(
                marker.BootSessionId,
                _currentBootSessionId,
                StringComparison.Ordinal));

    private bool IsRestartRequiredThisBoot(MutationVerificationMarker marker) =>
        marker.Phase != MutationVerificationPhase.VerificationPending
        && (_currentBootSessionId is null
            || string.Equals(
                marker.BootSessionId,
                _currentBootSessionId,
                StringComparison.Ordinal));

    private bool AddVerifiedOperation(Guid operationId)
    {
        if (operationId == Guid.Empty || !_verifiedOperationIds.Add(operationId))
        {
            return false;
        }

        _verifiedOperationOrder.Enqueue(operationId);
        while (_verifiedOperationOrder.Count > MaximumVerifiedOperationCount)
        {
            _verifiedOperationIds.Remove(_verifiedOperationOrder.Dequeue());
        }

        return true;
    }

    private bool SetMarker(MutationVerificationMarker marker)
    {
        if (_markers.TryGetValue(marker.Package.Key, out var existing)
            && existing == marker)
        {
            return false;
        }

        _markers[marker.Package.Key] = marker;
        return true;
    }

    private static PackageSummary CreateFallbackPackage(PackageKey package) => new()
    {
        Key = package,
        Name = package.Id,
        SourceName = package.SourceId,
        Status = PackageStatus.UpdateAvailable
    };

    private static PackageSummary CreateMarkerPackage(PackageSummary package) => new()
    {
        Key = package.Key,
        Name = package.Name,
        Publisher = package.Publisher,
        SourceName = package.SourceName,
        InstalledVersion = package.InstalledVersion,
        AvailableVersion = package.AvailableVersion,
        Status = PackageStatus.UpdateAvailable,
        ElevationRequirement = package.ElevationRequirement
    };
}

public enum MutationVerificationPhase
{
    OutcomeUnknown,
    VerificationPending,
    RestartRequired
}

public sealed record MutationVerificationMarker
{
    public Guid OperationId { get; init; }
    public Guid RevisionId { get; init; }
    public PackageOperationKind Kind { get; init; }
    public PackageSummary Package { get; init; } = new();
    public DateTimeOffset RecordedAt { get; init; }
    public string BootSessionId { get; init; } = string.Empty;
    public MutationVerificationPhase Phase { get; init; }
}

public sealed record MutationVerificationTarget(
    PackageKey Package,
    Guid OperationId,
    Guid RevisionId,
    PackageOperationKind Kind);
