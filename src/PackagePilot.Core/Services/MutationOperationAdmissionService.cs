using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

public sealed record MutationAdmission(
    PackageOperation Operation,
    PackageSummary Package);

public sealed record MutationAdmissionBatchResult(
    IReadOnlyList<Guid> OperationIds,
    int DuplicateCount);

/// <summary>
/// Durably covers every WinGet mutation with an outcome-unknown marker before admitting
/// any work to the execution queue. A batch uses one atomic pre-admission snapshot write.
/// </summary>
public sealed class MutationOperationAdmissionService
{
    private readonly IOperationQueue _operationQueue;
    private readonly MutationVerificationTracker _tracker;
    private readonly IMutationVerificationStore _store;

    public MutationOperationAdmissionService(
        IOperationQueue operationQueue,
        MutationVerificationTracker tracker,
        IMutationVerificationStore store)
    {
        _operationQueue = operationQueue ?? throw new ArgumentNullException(nameof(operationQueue));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public MutationAdmissionBatchResult Enqueue(
        IEnumerable<MutationAdmission> admissions,
        bool skipDuplicates = false)
    {
        ArgumentNullException.ThrowIfNull(admissions);
        if (!_tracker.CanAdmitMutations)
        {
            throw new MutationRecoveryUnavailableException(
                "The current Windows boot session could not be identified safely.");
        }

        var candidates = admissions.ToArray();
        if (candidates.Length == 0)
        {
            return new MutationAdmissionBatchResult([], 0);
        }

        var queue = _operationQueue.Snapshot;
        var seenTargets = new HashSet<PackageKey>();
        var seenOperationIds = new HashSet<Guid>();
        var selected = new List<MutationAdmission>(candidates.Length);
        var duplicateCount = 0;
        foreach (var admission in candidates)
        {
            ValidateAdmission(admission);
            if (!seenOperationIds.Add(admission.Operation.Id))
            {
                throw new ArgumentException(
                    $"Operation '{admission.Operation.Id}' occurs more than once in the admission batch.",
                    nameof(admissions));
            }

            var package = admission.Package.Key;
            var duplicate = !seenTargets.Add(package)
                || _tracker.Contains(package)
                || ContainsActiveOperation(queue, package);
            if (!duplicate)
            {
                selected.Add(admission);
                continue;
            }

            if (!skipDuplicates)
            {
                throw new DuplicatePackageOperationException(
                    FindActiveOperationId(queue, package),
                    package.Id);
            }

            duplicateCount++;
        }

        if (selected.Count == 0)
        {
            return new MutationAdmissionBatchResult([], duplicateCount);
        }

        var durableMarkers = new List<MutationVerificationMarker>(selected.Count);
        foreach (var admission in selected)
        {
            if (!_tracker.MarkEnqueued(admission.Operation, admission.Package))
            {
                // This can only be a same-thread duplicate after preflight. Restore the
                // tracker to its original state before surfacing the conflict.
                _tracker.RemoveMarkers(durableMarkers);
                throw new DuplicatePackageOperationException(
                    FindActiveOperationId(queue, admission.Package.Key),
                    admission.Package.Key.Id);
            }

            durableMarkers.Add(_tracker.GetMarker(admission.Package.Key));
        }

        try
        {
            _store.Save(_tracker.CreateSnapshot());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _tracker.RemoveMarkers(durableMarkers);
            throw new MutationRecoveryUnavailableException(
                "Package Pilot could not durably record package-operation recovery state.",
                exception);
        }

        var operationIds = new List<Guid>(selected.Count);
        var rejectedMarkers = new List<MutationVerificationMarker>();
        Exception? fatalQueueError = null;
        for (var index = 0; index < selected.Count; index++)
        {
            var admission = selected[index];
            try
            {
                operationIds.Add(_operationQueue.Enqueue(admission.Operation));
            }
            catch (DuplicatePackageOperationException) when (skipDuplicates)
            {
                duplicateCount++;
                rejectedMarkers.Add(durableMarkers[index]);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                fatalQueueError = exception;
                rejectedMarkers.AddRange(durableMarkers.Skip(index));
                break;
            }
        }

        if (rejectedMarkers.Count > 0)
        {
            _tracker.RemoveMarkers(rejectedMarkers);
            try
            {
                _store.Save(_tracker.CreateSnapshot());
            }
            catch (Exception rollbackException) when (rollbackException is not OutOfMemoryException)
            {
                // The pre-admission snapshot remains authoritative. Restore those markers
                // in memory as outcome-unknown so both this process and the next one fail closed.
                _tracker.RestoreMarkers(rejectedMarkers);
                throw new MutationRecoveryUnavailableException(
                    "Package Pilot could not safely roll back package-operation recovery state.",
                    rollbackException);
            }
        }

        if (fatalQueueError is not null)
        {
            throw fatalQueueError;
        }

        return new MutationAdmissionBatchResult(operationIds, duplicateCount);
    }

    private static void ValidateAdmission(MutationAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(admission.Operation);
        ArgumentNullException.ThrowIfNull(admission.Package);
        if (admission.Operation.Id == Guid.Empty
            || admission.Operation.EffectiveTarget is not WingetTarget target
            || target.Package != admission.Package.Key
            || target.Package.IsEmpty
            || string.IsNullOrWhiteSpace(target.Package.SourceId))
        {
            throw new ArgumentException(
                "A mutation admission must contain one exact, matching WinGet target.",
                nameof(admission));
        }
    }

    private static bool ContainsActiveOperation(
        OperationQueueSnapshot queue,
        PackageKey package) =>
        (queue.Current is { } current
            && IsMatchingOperation(current.Operation, package))
        || queue.Pending.Any(entry => IsMatchingOperation(entry.Operation, package));

    private static Guid FindActiveOperationId(
        OperationQueueSnapshot queue,
        PackageKey package)
    {
        if (queue.Current is { } current
            && IsMatchingOperation(current.Operation, package))
        {
            return current.Operation.Id;
        }

        return queue.Pending.FirstOrDefault(entry =>
            IsMatchingOperation(entry.Operation, package))?.Operation.Id ?? Guid.Empty;
    }

    private static bool IsMatchingOperation(
        PackageOperation operation,
        PackageKey package) =>
        operation.EffectiveTarget is WingetTarget target
        && target.Package == package;
}
