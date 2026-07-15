using System.Runtime.InteropServices;

namespace PackagePilot.Background;

public static class Program
{
    internal const string RegisterServerArgument = "-RegisterForBGTaskServer";

    private static readonly ManualResetEvent ExitEvent = new(initialState: false);

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
            ExitEvent.WaitOne();
            return 0;
        }
        finally
        {
            if (registrationToken != 0)
            {
                _ = ComServer.CoRevokeClassObject(registrationToken);
            }
        }
    }

    internal static void SignalExit() => ExitEvent.Set();
}
