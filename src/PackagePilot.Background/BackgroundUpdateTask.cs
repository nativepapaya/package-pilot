using System.Runtime.InteropServices;
using PackagePilot.Core.Models;
using Windows.ApplicationModel.Background;

namespace PackagePilot.Background;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("5C2B2D42-64E7-47DC-B966-1E408555A39B")]
[ComSourceInterfaces(typeof(IBackgroundTask))]
public sealed class BackgroundUpdateTask : IBackgroundTask
{
    internal static readonly Guid ClassId = new("5C2B2D42-64E7-47DC-B966-1E408555A39B");

    private readonly CancellationTokenSource _cancellation = new();
    private int _started;

    [MTAThread]
    public void Run(IBackgroundTaskInstance taskInstance)
    {
        ArgumentNullException.ThrowIfNull(taskInstance);
        Program.SignalActivation(RequestCancellation);
        BackgroundTaskDeferral deferral;
        try
        {
            deferral = taskInstance.GetDeferral();
        }
        catch (Exception exception)
        {
            _ = RecordFailureAndSignalExitAsync(exception);
            return;
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            try
            {
                deferral.Complete();
            }
            catch
            {
                // The first invocation still owns the host lifetime.
            }

            return;
        }

        try
        {
            TryReportProgress(taskInstance, 0);
            taskInstance.Canceled += OnCanceled;
            _ = RunAndCompleteAsync(taskInstance, deferral);
        }
        catch (Exception exception)
        {
            _ = RecordFailureAndCompleteAsync(taskInstance, deferral, exception);
        }
    }

    private async Task RunAndCompleteAsync(
        IBackgroundTaskInstance taskInstance,
        BackgroundTaskDeferral deferral)
    {
        try
        {
            TryReportProgress(taskInstance, 10);
            var status = await BackgroundHostFactory.CreateRunner()
                .RunOnceAsync(_cancellation.Token)
                .ConfigureAwait(false);
            // Progress is optional broker telemetry. In particular, the broker can revoke
            // this interface as the asynchronous operation completes. A late COM failure
            // must not replace the runner-authored result or turn a successful scan into a
            // host failure.
            TryReportProgress(
                taskInstance,
                status.State == BackgroundUpdateRunState.Cancelled ? 0u : 100u);
        }
        catch (Exception exception)
        {
            // BackgroundUpdateRunner records expected scan failures. This catches failures
            // before or around construction of that service graph.
            await BackgroundHostStatusReporter.TryRecordAsync(
                BackgroundUpdateRunState.Failed,
                exception).ConfigureAwait(false);
        }
        finally
        {
            CompleteHost(taskInstance, deferral);
        }
    }

    [MTAThread]
    private void OnCanceled(
        IBackgroundTaskInstance sender,
        BackgroundTaskCancellationReason reason) => RequestCancellation();

    private void RequestCancellation()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race with Windows or the host watchdog.
        }
    }

    private async Task RecordFailureAndCompleteAsync(
        IBackgroundTaskInstance taskInstance,
        BackgroundTaskDeferral deferral,
        Exception exception)
    {
        try
        {
            await BackgroundHostStatusReporter.TryRecordAsync(
                BackgroundUpdateRunState.Failed,
                exception).ConfigureAwait(false);
        }
        finally
        {
            CompleteHost(taskInstance, deferral);
        }
    }

    private static async Task RecordFailureAndSignalExitAsync(Exception exception)
    {
        try
        {
            await BackgroundHostStatusReporter.TryRecordAsync(
                BackgroundUpdateRunState.Failed,
                exception).ConfigureAwait(false);
        }
        finally
        {
            Program.SignalExit();
        }
    }

    private void CompleteHost(
        IBackgroundTaskInstance taskInstance,
        BackgroundTaskDeferral deferral)
    {
        try
        {
            try
            {
                taskInstance.Canceled -= OnCanceled;
            }
            catch
            {
                // COM cleanup must not strand the process.
            }

            _cancellation.Dispose();
            try
            {
                deferral.Complete();
            }
            catch
            {
                // Windows may already have cancelled or disconnected the task.
            }
        }
        finally
        {
            Program.SignalExit();
        }
    }

    private static void TryReportProgress(
        IBackgroundTaskInstance taskInstance,
        uint progress)
    {
        try
        {
            taskInstance.Progress = progress;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Progress is advisory. Discovery, its durable status, and deferral completion
            // remain authoritative if the broker has already disconnected this interface.
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}
