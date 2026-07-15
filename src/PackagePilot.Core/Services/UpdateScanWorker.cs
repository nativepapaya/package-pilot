using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Reusable foreground/background update worker. It performs discovery only; package and source
/// mutations are intentionally absent from this service graph.
/// </summary>
public sealed class UpdateScanWorker
{
    private readonly IUpdateCoordinator _coordinator;
    private readonly UpdateNotificationPolicy _notificationPolicy;
    private readonly IUpdateNotificationSink _notificationSink;
    private readonly CrossProcessUpdateScanMutex _scanMutex;

    public UpdateScanWorker(
        IUpdateCoordinator coordinator,
        UpdateNotificationPolicy notificationPolicy,
        IUpdateNotificationSink notificationSink,
        CrossProcessUpdateScanMutex? scanMutex = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _notificationPolicy = notificationPolicy ?? throw new ArgumentNullException(nameof(notificationPolicy));
        _notificationSink = notificationSink ?? throw new ArgumentNullException(nameof(notificationSink));
        _scanMutex = scanMutex ?? new CrossProcessUpdateScanMutex();
    }

    public Task<UpdateScanExecutionResult> RunAsync(
        UpdateCheckReason reason,
        bool isForegroundWindowActive,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses = null,
        CancellationToken cancellationToken = default) =>
        _scanMutex.RunExclusiveAsync(
            token => RunCoreAsync(reason, isForegroundWindowActive, sourceStatuses, token),
            cancellationToken);

    private async Task<UpdateScanExecutionResult> RunCoreAsync(
        UpdateCheckReason reason,
        bool isForegroundWindowActive,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses,
        CancellationToken cancellationToken)
    {
        var previous = await _coordinator.LoadAsync(cancellationToken).ConfigureAwait(false);
        var check = await _coordinator.CheckAsync(
            reason,
            sourceStatuses,
            cancellationToken).ConfigureAwait(false);

        var succeeded = check.State != UpdateCheckState.Failed;
        var decision = _notificationPolicy.Evaluate(
            previous?.Fingerprints,
            check.Snapshot.Fingerprints,
            isForegroundWindowActive,
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
