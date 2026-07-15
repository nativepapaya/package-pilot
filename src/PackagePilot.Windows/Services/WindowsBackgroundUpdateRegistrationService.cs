using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.Background;

namespace PackagePilot.Windows.Services;

/// <summary>Registers an opportunistic MSIX background trigger; it never schedules mutation work.</summary>
public sealed class WindowsBackgroundUpdateRegistrationService : IBackgroundUpdateRegistrationService
{
    private const uint DailyMinutes = 1_440;
    private const uint SixHourMinutes = 360;

    public async Task<BackgroundMonitoringResult> ConfigureAsync(
        UpdateMonitoringCadence cadence,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UnregisterExisting();

        if (cadence == UpdateMonitoringCadence.Manual)
        {
            return new BackgroundMonitoringResult
            {
                State = BackgroundMonitoringState.Disabled,
                Cadence = cadence,
                Message = "Background update discovery is disabled."
            };
        }

        try
        {
            var access = await BackgroundExecutionManager.RequestAccessAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAllowed(access))
            {
                return new BackgroundMonitoringResult
                {
                    State = BackgroundMonitoringState.Denied,
                    Cadence = cadence,
                    Message = "Windows did not allow Package Pilot to run update discovery in the background."
                };
            }

            var minutes = cadence == UpdateMonitoringCadence.EverySixHours
                ? SixHourMinutes
                : DailyMinutes;
            var builder = new BackgroundTaskBuilder
            {
                Name = WindowsIntegrationConstants.BackgroundTaskName,
                CancelOnConditionLoss = true
            };
            builder.SetTaskEntryPointClsid(WindowsIntegrationConstants.BackgroundTaskClassId);
            builder.SetTrigger(new TimeTrigger(minutes, oneShot: false));
            builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
            builder.Register();

            return new BackgroundMonitoringResult
            {
                State = BackgroundMonitoringState.Registered,
                Cadence = cadence,
                Message = cadence == UpdateMonitoringCadence.EverySixHours
                    ? "Windows will check approximately every six hours."
                    : "Windows will check approximately once per day."
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(BackgroundMonitoringState.Denied, cadence, ex.Message);
        }
        catch (COMException ex)
        {
            return Failed(BackgroundMonitoringState.Unavailable, cadence, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(BackgroundMonitoringState.Failed, cadence, ex.Message);
        }
    }

    public BackgroundMonitoringResult GetCurrent()
    {
        var registration = BackgroundTaskRegistration.AllTasks.Values
            .FirstOrDefault(item => string.Equals(
                item.Name,
                WindowsIntegrationConstants.BackgroundTaskName,
                StringComparison.Ordinal));
        if (registration is null)
        {
            return new BackgroundMonitoringResult
            {
                State = BackgroundMonitoringState.Disabled,
                Cadence = UpdateMonitoringCadence.Manual
            };
        }

        // Windows does not expose a TimeTrigger's interval through the registration. The
        // selected cadence remains authoritative in app settings.
        return new BackgroundMonitoringResult
        {
            State = BackgroundMonitoringState.Registered,
            Cadence = UpdateMonitoringCadence.Daily
        };
    }

    private static void UnregisterExisting()
    {
        foreach (var registration in BackgroundTaskRegistration.AllTasks.Values
                     .Where(item => string.Equals(
                         item.Name,
                         WindowsIntegrationConstants.BackgroundTaskName,
                         StringComparison.Ordinal))
                     .ToArray())
        {
            registration.Unregister(cancelTask: true);
        }
    }

    private static bool IsAllowed(BackgroundAccessStatus status) => status is
        BackgroundAccessStatus.AllowedSubjectToSystemPolicy or
        BackgroundAccessStatus.AlwaysAllowed;

    private static BackgroundMonitoringResult Failed(
        BackgroundMonitoringState state,
        UpdateMonitoringCadence cadence,
        string message) => new()
        {
            State = state,
            Cadence = cadence,
            Message = message
        };
}
