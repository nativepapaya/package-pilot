using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Applies update freshness and retry policy, coalesces concurrent checks, and preserves the
/// last successful rows when a later check fails.
/// </summary>
public sealed class UpdateCoordinator : IUpdateCoordinator
{
    public static readonly TimeSpan DefaultFreshness = UpdateMonitoringPolicy.DailyFreshness;
    public static readonly TimeSpan DefaultFailureRetryDelay = UpdateMonitoringPolicy.FailureRetryDelay;

    private readonly IUpdateDiscoveryClient _updateDiscoveryClient;
    private readonly IUpdateSnapshotStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _freshnessOverride;
    private readonly TimeSpan _failureRetryDelay;
    private readonly object _checkGate = new();
    private Task<UpdateCheckResult>? _activeCheck;

    public UpdateCoordinator(
        IUpdateDiscoveryClient updateDiscoveryClient,
        IUpdateSnapshotStore store,
        TimeProvider? timeProvider = null,
        TimeSpan? freshness = null,
        TimeSpan? failureRetryDelay = null)
    {
        _updateDiscoveryClient = updateDiscoveryClient
            ?? throw new ArgumentNullException(nameof(updateDiscoveryClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _freshnessOverride = freshness;
        _failureRetryDelay = failureRetryDelay ?? DefaultFailureRetryDelay;

        if (_freshnessOverride is { } configuredFreshness)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuredFreshness, TimeSpan.Zero);
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_failureRetryDelay, TimeSpan.Zero);
    }

    public Task<UpdateSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
        _store.LoadAsync(cancellationToken);

    public UpdateCheckState GetState(
        UpdateSnapshot? snapshot,
        UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily) =>
        UpdateMonitoringPolicy.GetState(
            snapshot,
            cadence,
            _timeProvider.GetUtcNow(),
            _freshnessOverride);

    public bool ShouldAutomaticallyCheck(
        UpdateSnapshot? snapshot,
        UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily) =>
        UpdateMonitoringPolicy.ShouldAutomaticallyCheck(
            snapshot,
            cadence,
            _timeProvider.GetUtcNow(),
            _freshnessOverride,
            _failureRetryDelay);

    public Task<UpdateCheckResult> CheckAsync(
        UpdateCheckReason reason,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses = null,
        CancellationToken cancellationToken = default,
        UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily)
    {
        Task<UpdateCheckResult> check;
        lock (_checkGate)
        {
            if (_activeCheck is not null)
            {
                return WaitForActiveThenRetryIfNeededAsync(
                    _activeCheck,
                    reason,
                    sourceStatuses,
                    cancellationToken,
                    cadence);
            }

            check = CheckAndReleaseAsync(reason, sourceStatuses, cancellationToken, cadence);
            _activeCheck = check;
        }

        return check;
    }

    private async Task<UpdateCheckResult> WaitForActiveThenRetryIfNeededAsync(
        Task<UpdateCheckResult> activeCheck,
        UpdateCheckReason reason,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses,
        CancellationToken cancellationToken,
        UpdateMonitoringCadence cadence)
    {
        var result = await activeCheck.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (result.PerformedCheck && reason != UpdateCheckReason.PackageMutation)
        {
            return result;
        }

        var mustRetry = reason != UpdateCheckReason.Automatic
            || ShouldAutomaticallyCheck(result.Snapshot, cadence);
        return mustRetry
            ? await CheckAsync(reason, sourceStatuses, cancellationToken, cadence)
                .ConfigureAwait(false)
            : result;
    }

    private async Task<UpdateCheckResult> CheckAndReleaseAsync(
        UpdateCheckReason reason,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses,
        CancellationToken cancellationToken,
        UpdateMonitoringCadence cadence)
    {
        // Ensure _activeCheck is assigned before this method can reach its finally block.
        await Task.Yield();

        try
        {
            return await CheckCoreAsync(reason, sourceStatuses, cancellationToken, cadence).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        UpdateMonitoringCadence cadence)
    {
        var previous = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (reason == UpdateCheckReason.Automatic && !ShouldAutomaticallyCheck(previous, cadence))
        {
            return new UpdateCheckResult
            {
                Snapshot = previous ?? new UpdateSnapshot(),
                State = GetState(previous, cadence),
                PerformedCheck = false
            };
        }

        try
        {
            var updates = await _updateDiscoveryClient
                .GetAvailableUpdatesAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var completedAt = _timeProvider.GetUtcNow();

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
                // Freshness and retry windows start when discovery completes. Using the
                // start time can make a long-running check stale (or retryable) immediately.
                LastAttemptAt = completedAt,
                LastSuccessAt = completedAt,
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
            var failedAt = _timeProvider.GetUtcNow();
            var snapshot = (previous ?? new UpdateSnapshot()) with
            {
                LastAttemptAt = failedAt,
                LastError = NormalizeError(exception.Message),
                Sources = CaptureFailureSources(previous, sourceStatuses)
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

    private static IReadOnlyList<UpdateSourceHealthSnapshot> CaptureFailureSources(
        UpdateSnapshot? previous,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses) =>
        sourceStatuses is { Count: > 0 }
            ? CaptureSources(sourceStatuses, previous?.Updates ?? Array.Empty<PackageSummary>())
            : previous?.Sources ?? Array.Empty<UpdateSourceHealthSnapshot>();

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
