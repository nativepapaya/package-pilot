using System.Runtime.InteropServices;

namespace PackagePilot.Background;

public static class Program
{
    internal const string RegisterServerArgument = "-RegisterForBGTaskServer";
    internal static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MaximumHostLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan CancellationGracePeriod = TimeSpan.FromSeconds(5);

    private static readonly ManualResetEvent ExitEvent = new(initialState: false);
    private static readonly ManualResetEvent ActivationEvent = new(initialState: false);
    private static Action? _cancelActiveTask;

    [MTAThread]
    public static int Main(string[] args)
    {
        if (!args.Any(argument => string.Equals(
                argument,
                RegisterServerArgument,
                StringComparison.OrdinalIgnoreCase)))
        {
            // This executable is a package-owned COM host, not a user-facing CLI.
            return 2;
        }

        var classId = BackgroundUpdateTask.ClassId;
        var hresult = ComServer.CoRegisterClassObject(
            ref classId,
            new ComServer.BackgroundTaskFactory(),
            ComServer.ClsctxLocalServer,
            ComServer.RegclsSingleUse,
            out var registrationToken);
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }

        try
        {
            int initialSignal = WaitHandle.WaitAny(
                [ExitEvent, ActivationEvent],
                ActivationTimeout);
            if (initialSignal == WaitHandle.WaitTimeout || initialSignal == 0)
            {
                // A package-owned COM server should be activated immediately by Windows.
                // A direct/manual launch must not become an idle resident process.
                return 0;
            }

            if (ExitEvent.WaitOne(MaximumHostLifetime))
            {
                return 0;
            }

            // The background graph is read-only. Bound even a wedged WinGet activation so
            // PackagePilot.Background can never become an idle orphan.
            RequestActiveTaskCancellation();
            if (ExitEvent.WaitOne(CancellationGracePeriod))
            {
                return 0;
            }

            BackgroundHostStatusReporter.TryRecordAsync(
                    Core.Models.BackgroundUpdateRunState.Cancelled,
                    "The background host exceeded its maximum lifetime and was stopped.")
                .Wait(TimeSpan.FromSeconds(1));
            return 1;
        }
        finally
        {
            Interlocked.Exchange(ref _cancelActiveTask, null);
            if (registrationToken != 0)
            {
                _ = ComServer.CoRevokeClassObject(registrationToken);
            }
        }
    }

    internal static void SignalActivation(Action cancelActiveTask)
    {
        ArgumentNullException.ThrowIfNull(cancelActiveTask);
        Interlocked.Exchange(ref _cancelActiveTask, cancelActiveTask);
        ActivationEvent.Set();
    }

    internal static void SignalExit() => ExitEvent.Set();

    private static void RequestActiveTaskCancellation()
    {
        try
        {
            Volatile.Read(ref _cancelActiveTask)?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // Completion raced the watchdog; ExitEvent will already be set or follow shortly.
        }
    }
}
