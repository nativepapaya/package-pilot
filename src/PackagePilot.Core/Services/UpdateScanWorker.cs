using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Reusable foreground/background update worker. It performs discovery only; package and source
/// mutations are intentionally absent from this service graph.
/// </summary>
public sealed class UpdateScanWorker
{
    public static readonly TimeSpan AutomaticMutexTimeout = TimeSpan.FromSeconds(2);

    private readonly IUpdateCoordinator _coordinator;
    private readonly UpdateNotificationPolicy _notificationPolicy;
    private readonly IUpdateNotificationSink _notificationSink;
    private readonly CrossProcessUpdateScanMutex _scanMutex;
    private readonly TimeSpan _automaticMutexTimeout;

    public UpdateScanWorker(
        IUpdateCoordinator coordinator,
        UpdateNotificationPolicy notificationPolicy,
        IUpdateNotificationSink notificationSink,
        CrossProcessUpdateScanMutex? scanMutex = null,
        TimeSpan? automaticMutexTimeout = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _notificationPolicy = notificationPolicy ?? throw new ArgumentNullException(nameof(notificationPolicy));
        _notificationSink = notificationSink ?? throw new ArgumentNullException(nameof(notificationSink));
        _scanMutex = scanMutex ?? new CrossProcessUpdateScanMutex();
        _automaticMutexTimeout = automaticMutexTimeout ?? AutomaticMutexTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _automaticMutexTimeout,
            TimeSpan.Zero);
    }

    public Task<UpdateScanExecutionResult> RunAsync(
        UpdateCheckReason reason,
        bool isForegroundWindowActive,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses = null,
        CancellationToken cancellationToken = default,
        UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily,
        bool retryPendingNotification = false) =>
        RunSerializedAsync(
            reason,
            () => isForegroundWindowActive,
            sourceStatuses,
            cancellationToken,
            cadence,
            retryPendingNotification);

    public Task<UpdateScanExecutionResult> RunAsync(
        UpdateCheckReason reason,
        IWindowActivityService windowActivityService,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses = null,
        CancellationToken cancellationToken = default,
        UpdateMonitoringCadence cadence = UpdateMonitoringCadence.Daily,
        bool retryPendingNotification = false)
    {
        ArgumentNullException.ThrowIfNull(windowActivityService);
        return RunSerializedAsync(
            reason,
            () => windowActivityService.Current.IsForegroundActive,
            sourceStatuses,
            cancellationToken,
            cadence,
            retryPendingNotification);
    }

    private async Task<UpdateScanExecutionResult> RunSerializedAsync(
        UpdateCheckReason reason,
        Func<bool> isForegroundWindowActive,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses,
        CancellationToken cancellationToken,
        UpdateMonitoringCadence cadence,
        bool retryPendingNotification)
    {
        try
        {
            return await _scanMutex.RunExclusiveAsync(
                token => RunCoreAsync(
                    reason,
                    isForegroundWindowActive,
                    sourceStatuses,
                    token,
                    cadence,
                    retryPendingNotification),
                cancellationToken,
                reason == UpdateCheckReason.Automatic ? _automaticMutexTimeout : null)
                .ConfigureAwait(false);
        }
        catch (CrossProcessUpdateScanBusyException)
            when (reason == UpdateCheckReason.Automatic)
        {
            var snapshot = await _coordinator.LoadAsync(cancellationToken).ConfigureAwait(false);
            var fingerprints = snapshot?.Fingerprints ?? Array.Empty<UpdateFingerprint>();
            return new UpdateScanExecutionResult
            {
                State = UpdateScanExecutionState.SkippedBusy,
                Check = new UpdateCheckResult
                {
                    Snapshot = snapshot ?? new UpdateSnapshot(),
                    State = _coordinator.GetState(snapshot, cadence),
                    PerformedCheck = false
                },
                Notification = _notificationPolicy.Evaluate(
                    fingerprints,
                    fingerprints,
                    isForegroundWindowActive()),
                NotificationApplied = false
            };
        }
    }

    private async Task<UpdateScanExecutionResult> RunCoreAsync(
        UpdateCheckReason reason,
        Func<bool> isForegroundWindowActive,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses,
        CancellationToken cancellationToken,
        UpdateMonitoringCadence cadence,
        bool retryPendingNotification)
    {
        var previous = await _coordinator.LoadAsync(cancellationToken).ConfigureAwait(false);
        var check = await _coordinator.CheckAsync(
            reason,
            sourceStatuses,
            cancellationToken,
            cadence).ConfigureAwait(false);

        // Only a current snapshot is authoritative enough to clear a badge or toast. An
        // automatic request can be skipped as NotChecked/Stale when the cadence changes to
        // Manual between scheduling and execution; that must not look like a zero-update scan.
        var succeeded = check.State == UpdateCheckState.Current;
        IEnumerable<UpdateFingerprint>? notificationBaseline =
            retryPendingNotification && succeeded
                ? Array.Empty<UpdateFingerprint>()
                : previous?.Fingerprints;
        var decision = _notificationPolicy.Evaluate(
            notificationBaseline,
            check.Snapshot.Fingerprints,
            isForegroundWindowActive(),
            succeeded);

        try
        {
            await _notificationSink.ApplyAsync(decision, cancellationToken).ConfigureAwait(false);
            return new UpdateScanExecutionResult
            {
                Check = check,
                Notification = decision,
                NotificationApplied = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Notification registration must not turn a successful, persisted scan into a
            // failed update check. The background status records this separately.
            return new UpdateScanExecutionResult
            {
                Check = check,
                Notification = decision,
                NotificationApplied = false,
                NotificationError = NormalizeError(exception.Message)
            };
        }
    }

    internal static string NormalizeError(string? message)
    {
        var normalized = string.Join(' ', (message ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "The background update check failed.";
        }

        return normalized.Length <= 512 ? normalized : normalized[..512];
    }
}
