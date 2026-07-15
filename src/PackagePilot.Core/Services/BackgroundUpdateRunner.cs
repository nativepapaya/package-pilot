using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>Runs and records one opportunistic background update check.</summary>
public sealed class BackgroundUpdateRunner
{
    private readonly UpdateScanWorker _worker;
    private readonly IBackgroundUpdateRunStatusStore _statusStore;
    private readonly TimeProvider _timeProvider;

    public BackgroundUpdateRunner(
        UpdateScanWorker worker,
        IBackgroundUpdateRunStatusStore statusStore,
        TimeProvider? timeProvider = null)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BackgroundUpdateRunStatus> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var attemptedAt = _timeProvider.GetUtcNow();
        BackgroundUpdateRunStatus? previous = null;

        try
        {
            previous = await SafeLoadStatusAsync(cancellationToken).ConfigureAwait(false);
            var result = await _worker.RunAsync(
                UpdateCheckReason.Automatic,
                isForegroundWindowActive: false,
                sourceStatuses: null,
                cancellationToken).ConfigureAwait(false);
            var completedAt = _timeProvider.GetUtcNow();
            var failed = result.Check.State == UpdateCheckState.Failed;
            var performed = result.Check.PerformedCheck;
            var status = new BackgroundUpdateRunStatus
            {
                State = failed
                    ? BackgroundUpdateRunState.Failed
                    : performed
                        ? BackgroundUpdateRunState.Completed
                        : BackgroundUpdateRunState.Skipped,
                AttemptedAt = attemptedAt,
                CompletedAt = completedAt,
                LastSuccessfulRunAt = failed
                    ? previous?.LastSuccessfulRunAt
                    : performed
                        ? completedAt
                        : previous?.LastSuccessfulRunAt,
                UpdateCount = result.Check.Snapshot.Fingerprints.Count,
                ForegroundFallbackRequired = failed,
                Message = failed
                    ? UpdateScanWorker.NormalizeError(result.Check.Snapshot.LastError)
                    : result.NotificationError
            };

            await SafeSaveStatusAsync(status, cancellationToken).ConfigureAwait(false);
            return status;
        }
        catch (OperationCanceledException)
        {
            var status = new BackgroundUpdateRunStatus
            {
                State = BackgroundUpdateRunState.Cancelled,
                AttemptedAt = attemptedAt,
                CompletedAt = _timeProvider.GetUtcNow(),
                LastSuccessfulRunAt = previous?.LastSuccessfulRunAt,
                UpdateCount = previous?.UpdateCount ?? 0,
                Message = "Windows cancelled the background update check."
            };
            await SafeSaveStatusAsync(status, CancellationToken.None).ConfigureAwait(false);
            return status;
        }
        catch (Exception exception)
        {
            var status = new BackgroundUpdateRunStatus
            {
                State = BackgroundUpdateRunState.Failed,
                AttemptedAt = attemptedAt,
                CompletedAt = _timeProvider.GetUtcNow(),
                LastSuccessfulRunAt = previous?.LastSuccessfulRunAt,
                UpdateCount = previous?.UpdateCount ?? 0,
                ForegroundFallbackRequired = true,
                Message = UpdateScanWorker.NormalizeError(exception.Message)
            };
            await SafeSaveStatusAsync(status, CancellationToken.None).ConfigureAwait(false);
            return status;
        }
    }

    private async Task<BackgroundUpdateRunStatus?> SafeLoadStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _statusStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task SafeSaveStatusAsync(
        BackgroundUpdateRunStatus status,
        CancellationToken cancellationToken)
    {
        try
        {
            await _statusStore.SaveAsync(status, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The status is diagnostic; cancellation must not delay task completion.
        }
        catch
        {
            // A diagnostics write must never keep the background host alive.
        }
    }
}
