using System.Runtime.InteropServices;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using Windows.ApplicationModel;

namespace PackagePilot.Windows.Services;

/// <summary>Controls the opt-in, manifest-declared packaged login task.</summary>
public sealed class WindowsStartupRegistrationService : IStartupRegistrationService
{
    private readonly string _taskId;

    public WindowsStartupRegistrationService(string? taskId = null)
    {
        _taskId = string.IsNullOrWhiteSpace(taskId)
            ? WindowsIntegrationConstants.StartupTaskId
            : taskId.Trim();
    }

    public async Task<StartupRegistrationResult> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var task = await StartupTask.GetAsync(_taskId);
            cancellationToken.ThrowIfCancellationRequested();
            return CreateResult(task.State);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Failed(exception);
        }
    }

    public async Task<StartupRegistrationResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var task = await StartupTask.GetAsync(_taskId);
            cancellationToken.ThrowIfCancellationRequested();

            if (enabled)
            {
                if (task.State == StartupTaskState.Disabled)
                {
                    var state = await task.RequestEnableAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    return CreateResult(state);
                }

                return CreateResult(task.State);
            }

            if (task.State == StartupTaskState.Enabled)
            {
                task.Disable();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CreateResult(task.State);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Failed(exception);
        }
    }

    internal static StartupRegistrationState MapState(StartupTaskState state) => state switch
    {
        StartupTaskState.Disabled => StartupRegistrationState.Disabled,
        StartupTaskState.Enabled => StartupRegistrationState.Enabled,
        StartupTaskState.DisabledByUser => StartupRegistrationState.DisabledByUser,
        StartupTaskState.DisabledByPolicy => StartupRegistrationState.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => StartupRegistrationState.EnabledByPolicy,
        _ => StartupRegistrationState.Failed
    };

    private static StartupRegistrationResult CreateResult(StartupTaskState state)
    {
        var mapped = MapState(state);
        return new StartupRegistrationResult
        {
            State = mapped,
            Message = mapped switch
            {
                StartupRegistrationState.Disabled => "Start with Windows is off.",
                StartupRegistrationState.Enabled => "Package Pilot will start when you sign in.",
                StartupRegistrationState.DisabledByUser =>
                    "Windows Startup apps settings has disabled Package Pilot. Only you can enable it there.",
                StartupRegistrationState.DisabledByPolicy =>
                    "Start with Windows is disabled by organization policy.",
                StartupRegistrationState.EnabledByPolicy =>
                    "Start with Windows is enabled by organization policy.",
                _ => "Windows returned an unknown startup-task state."
            }
        };
    }

    private static StartupRegistrationResult Failed(Exception exception)
    {
        bool unavailable = exception is ArgumentException or FileNotFoundException or COMException;
        return new StartupRegistrationResult
        {
            State = unavailable
                ? StartupRegistrationState.Unavailable
                : StartupRegistrationState.Failed,
            Message = $"Windows could not access the Package Pilot startup task (0x{exception.HResult:X8})."
        };
    }

    private static bool IsRecoverable(Exception exception) => exception is not
        OperationCanceledException and not
        OutOfMemoryException and not
        StackOverflowException and not
        AccessViolationException;
}
