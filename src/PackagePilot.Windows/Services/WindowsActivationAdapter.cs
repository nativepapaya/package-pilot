using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using Windows.ApplicationModel.Activation;

namespace PackagePilot.Windows.Services;

/// <summary>Converts Windows activation payloads into the Core activation allowlist.</summary>
public sealed class WindowsActivationAdapter
{
    private readonly ActivationRouter _router = new();

    public ActivationParseResult Parse(AppActivationArguments? activation)
    {
        if (activation is null)
        {
            return ActivationParseResult.Accepted(new AppActivationRequest());
        }

        return activation.Kind switch
        {
            ExtendedActivationKind.Protocol when activation.Data is IProtocolActivatedEventArgs protocol =>
                _router.ParseProtocol(protocol.Uri),
            ExtendedActivationKind.AppNotification when activation.Data is AppNotificationActivatedEventArgs notification =>
                _router.ParseNotificationArguments(notification.Argument),
            ExtendedActivationKind.CommandLineLaunch when activation.Data is ICommandLineActivatedEventArgs commandLine =>
                ParseArguments(commandLine.Operation.Arguments),
            ExtendedActivationKind.Launch when activation.Data is ILaunchActivatedEventArgs launch =>
                ParseArguments(launch.Arguments),
            ExtendedActivationKind.Launch =>
                ActivationParseResult.Accepted(new AppActivationRequest()),
            _ => ActivationParseResult.Rejected("This Windows activation type is not supported.")
        };
    }

    public ActivationParseResult ParseNotification(AppNotificationActivatedEventArgs args) =>
        _router.ParseNotificationArguments(args.Argument);

    private ActivationParseResult ParseArguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return _router.ParseCommandLine([]);
        }

        var arguments = SplitWindowsCommandLine(commandLine);
        if (arguments.Count > 0 && IsExecutable(arguments[0]))
        {
            arguments.RemoveAt(0);
        }

        return _router.ParseCommandLine(arguments);
    }

    private static bool IsExecutable(string value) =>
        string.Equals(Path.GetFileName(value), "packagepilot.exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileName(value), "PackagePilot.App.exe", StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitWindowsCommandLine(string commandLine)
    {
        // CommandLineToArgvW applies the same quoting and backslash rules as the Windows
        // shell, avoiding a second, subtly different command language at the app boundary.
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == nint.Zero)
        {
            return [];
        }

        try
        {
            var result = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                var value = Marshal.ReadIntPtr(argv, index * nint.Size);
                result.Add(Marshal.PtrToStringUni(value) ?? string.Empty);
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
