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
        var deferral = taskInstance.GetDeferral();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            deferral.Complete();
            Program.SignalExit();
            return;
        }

        taskInstance.Progress = 0;
        taskInstance.Canceled += OnCanceled;
        _ = RunAndCompleteAsync(taskInstance, deferral);
    }

    private async Task RunAndCompleteAsync(
        IBackgroundTaskInstance taskInstance,
        BackgroundTaskDeferral deferral)
    {
        try
        {
            taskInstance.Progress = 10;
            var status = await BackgroundHostFactory.CreateRunner()
                .RunOnceAsync(_cancellation.Token)
                .ConfigureAwait(false);
            taskInstance.Progress = status.State == BackgroundUpdateRunState.Cancelled ? 0u : 100u;
        }
        catch
        {
            // BackgroundUpdateRunner records expected scan failures. Any failure before its
            // service graph exists is non-actionable here; the foreground freshness policy
            // will perform the deferred check on the next launch.
        }
        finally
        {
            taskInstance.Canceled -= OnCanceled;
            _cancellation.Dispose();
            deferral.Complete();
            Program.SignalExit();
        }
    }

    [MTAThread]
    private void OnCanceled(
        IBackgroundTaskInstance sender,
        BackgroundTaskCancellationReason reason) => _cancellation.Cancel();
}
