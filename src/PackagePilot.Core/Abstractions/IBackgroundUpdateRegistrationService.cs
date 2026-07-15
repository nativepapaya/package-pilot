using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

public interface IBackgroundUpdateRegistrationService
{
    Task<BackgroundMonitoringResult> ConfigureAsync(
        UpdateMonitoringCadence cadence,
        CancellationToken cancellationToken = default);

    BackgroundMonitoringResult GetCurrent();

    BackgroundMonitoringStatus GetStatus(BackgroundUpdateRunStatus? lastRun = null);
}
