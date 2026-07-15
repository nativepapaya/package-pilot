using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Applies update freshness and retry policy, coalesces concurrent checks, and preserves the
/// last successful rows when a later check fails.
/// </summary>
public sealed class UpdateCoordinator : IUpdateCoordinator
{
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultFailureRetryDelay = TimeSpan.FromHours(1);

    private readonly IWingetClient _wingetClient;
    private readonly IUpdateSnapshotStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _freshness;
    private readonly TimeSpan _failureRetryDelay;
    private readonly object _checkGate = new();
    private Task<UpdateCheckResult>? _activeCheck;

    public UpdateCoordinator(
        IWingetClient wingetClient,
        IUpdateSnapshotStore store,
        TimeProvider? timeProvider = null,
        TimeSpan? freshness = null,
        TimeSpan? failureRetryDelay = null)
    {
        _wingetClient = wingetClient ?? throw new ArgumentNullException(nameof(wingetClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _freshness = freshness ?? DefaultFreshness;
        _failureRetryDelay = failureRetryDelay ?? DefaultFailureRetryDelay;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_freshness, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_failureRetryDelay, TimeSpan.Zero);
    }

    public Task<UpdateSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(cancellationToken);

    public UpdateCheckState GetState(UpdateSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return UpdateCheckState.NotChecked;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastError)
            && snapshot.LastAttemptAt is { } failedAt
            && (snapshot.LastSuccessAt is null || failedAt >= snapshot.LastSuccessAt.Value))
        {
            return UpdateCheckState.Failed;
        }

        if (snapshot.LastSuccessAt is not { } succeededAt)
        {
            return UpdateCheckState.NotChecked;
        }

        return _timeProvider.GetUtcNow() >= succeededAt + _freshness
            ? UpdateCheckState.Stale
            : UpdateCheckState.Current;
    }

    public bool ShouldAutomaticallyCheck(UpdateSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return true;
        }

        if (GetState(snapshot) == UpdateCheckState.Failed
            && snapshot.LastAttemptAt is { } attemptedAt
            && _timeProvider.GetUtcNow() < attemptedAt + _failureRetryDelay)
        {
            return false;
        }

        return GetState(snapshot) is UpdateCheckState.NotChecked or UpdateCheckState.Stale or UpdateCheckState.Failed;
    }

    public Task<UpdateCheckResult> CheckAsync(
        UpdateCheckReason reason,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses = null,
        CancellationToken cancellationToken = default)
    {
        Task<UpdateCheckResult> check;
        lock (_checkGate)
        {
            if (_activeCheck is not null)
            {
                return _activeCheck.WaitAsync(cancellationToken);
            }

            check = CheckAndReleaseAsync(reason, sourceStatuses, cancellationToken);
            _activeCheck = check;
        }

        return check;
    }

    private async Task<UpdateCheckResult> CheckAndReleaseAsync(
        UpdateCheckReason reason,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses,
        CancellationToken cancellationToken)
    {
        // Ensure _activeCheck is assigned before this method can reach its finally block.
        await Task.Yield();

        try
        {
            return await CheckCoreAsync(reason, sourceStatuses, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_checkGate)
            {
                _activeCheck = null;
            }
        }
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(
        UpdateCheckReason reason,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses,
        CancellationToken cancellationToken)
    {
        var previous = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (reason == UpdateCheckReason.Automatic && !ShouldAutomaticallyCheck(previous))
        {
            return new UpdateCheckResult
            {
                Snapshot = previous ?? new UpdateSnapshot(),
                State = GetState(previous),
                PerformedCheck = false
            };
        }

        var attemptedAt = _timeProvider.GetUtcNow();
        try
        {
            var updates = await _wingetClient.GetAvailableUpdatesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedUpdates = updates
                .GroupBy(
                    package => new
                    {
                        Id = package.Key.Id.Trim().ToLowerInvariant(),
                        Source = package.Key.SourceId.Trim().ToLowerInvariant(),
                        Version = package.AvailableVersion?.Trim() ?? string.Empty
                    })
                .Select(group => group.First())
                .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Key.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Key.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var snapshot = new UpdateSnapshot
            {
                LastAttemptAt = attemptedAt,
                LastSuccessAt = attemptedAt,
                Updates = normalizedUpdates,
                Sources = CaptureSources(sourceStatuses, normalizedUpdates),
                Fingerprints = normalizedUpdates
                    .Select(CreateFingerprint)
                    .OrderBy(item => item.SourceId, StringComparer.Ordinal)
                    .ThenBy(item => item.PackageId, StringComparer.Ordinal)
                    .ThenBy(item => item.AvailableVersion, StringComparer.Ordinal)
                    .ToArray()
            };

            await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return new UpdateCheckResult
            {
                Snapshot = snapshot,
                State = UpdateCheckState.Current,
                PerformedCheck = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var snapshot = (previous ?? new UpdateSnapshot()) with
            {
                LastAttemptAt = attemptedAt,
                LastError = NormalizeError(exception.Message)
            };

            try
            {
                await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // The check result is still useful for this process even if its cache cannot be updated.
            }

            return new UpdateCheckResult
            {
                Snapshot = snapshot,
                State = UpdateCheckState.Failed,
                PerformedCheck = true
            };
        }
    }

    private static IReadOnlyList<UpdateSourceHealthSnapshot> CaptureSources(
        IReadOnlyList<PackageSourceStatus>? sourceStatuses,
        IReadOnlyList<PackageSummary> updates)
    {
        if (sourceStatuses is { Count: > 0 })
        {
            return sourceStatuses
                .Select(source => new UpdateSourceHealthSnapshot
                {
                    Id = source.Id,
                    Name = source.Name,
                    Health = source.Health,
                    Message = source.Message
                })
                .OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return updates
            .GroupBy(
                package => package.Key.SourceId,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new UpdateSourceHealthSnapshot
            {
                Id = group.Key,
                Name = group.Select(package => package.SourceName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key,
                Health = SourceHealth.Healthy
            })
            .OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static UpdateFingerprint CreateFingerprint(PackageSummary package) => new()
    {
        SourceId = package.Key.SourceId.Trim().ToLowerInvariant(),
        PackageId = package.Key.Id.Trim().ToLowerInvariant(),
        AvailableVersion = package.AvailableVersion?.Trim() ?? string.Empty
    };

    private static string NormalizeError(string message)
    {
        var normalized = string.Join(' ', message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "The update check failed.";
        }

        return normalized.Length <= 512 ? normalized : normalized[..512];
    }
}
