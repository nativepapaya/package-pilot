using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using PackagePilot.Windows.Services;

namespace PackagePilot.App;

/// <summary>
/// Resolves single-instance activation before WinUI creates the application.
/// </summary>
internal static class Program
{
    private const uint RedirectTimeoutMilliseconds = 5_000;
    private const int MaximumPendingActivations = 16;
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconError = 0x00000010;
    private static readonly object ActivationGate = new();
    private static readonly Queue<AppActivationArguments> PendingActivations = new();
    private static Action<AppActivationArguments>? _redirectedActivationHandler;
    private static AppInstance? _mainInstance;
    private static bool _shutdownStarted;

    internal static AppActivationArguments? InitialActivation { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        InitialActivation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance? registeredInstance = null;
        RedirectResult? redirectFailure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            registeredInstance = AppInstance.FindOrRegisterForKey(
                WindowsIntegrationConstants.MainInstanceKey);
            if (registeredInstance.IsCurrent)
            {
                break;
            }

            redirectFailure = RedirectActivationTo(InitialActivation!, registeredInstance);
            if (redirectFailure.Succeeded)
            {
                return 0;
            }

            if (attempt == 0 && !IsProcessRunning(registeredInstance.ProcessId))
            {
                continue;
            }

            ShowRedirectionFailure(redirectFailure);
            return 1;
        }

        if (registeredInstance is null || !registeredInstance.IsCurrent)
        {
            ShowRedirectionFailure(redirectFailure ?? RedirectResult.Failed(
                "Windows did not grant Package Pilot ownership of its application instance."));
            return 1;
        }

        _mainInstance = registeredInstance;
        _mainInstance.Activated += OnAppInstanceActivated;

        try
        {
            Application.Start(initializationCallbackParams =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        finally
        {
            BeginShutdown();
        }

        return 0;
    }

    internal static void SetRedirectedActivationHandler(
        Action<AppActivationArguments>? handler)
    {
        AppActivationArguments[] pending = [];
        lock (ActivationGate)
        {
            if (_shutdownStarted)
            {
                PendingActivations.Clear();
                return;
            }

            _redirectedActivationHandler = handler;
            if (handler is not null && PendingActivations.Count > 0)
            {
                pending = PendingActivations.ToArray();
                PendingActivations.Clear();
            }
        }

        if (handler is null)
        {
            return;
        }

        foreach (AppActivationArguments activation in pending)
        {
            handler(activation);
        }
    }

    internal static void BeginShutdown()
    {
        AppInstance? mainInstance;
        lock (ActivationGate)
        {
            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
            mainInstance = _mainInstance;
        }

        try
        {
            // Release single-instance ownership before detaching the live handler. This
            // minimizes the interval in which Windows can route work to an exiting owner.
            mainInstance?.UnregisterKey();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Process exit releases stale ownership if Windows rejected explicit cleanup.
        }
        finally
        {
            if (mainInstance is not null)
            {
                mainInstance.Activated -= OnAppInstanceActivated;
            }

            lock (ActivationGate)
            {
                _redirectedActivationHandler = null;
                PendingActivations.Clear();
                _mainInstance = null;
            }
        }
    }

    private static void OnAppInstanceActivated(
        object? sender,
        AppActivationArguments activation)
    {
        Action<AppActivationArguments>? handler;
        bool rejectedDuringShutdown;
        lock (ActivationGate)
        {
            rejectedDuringShutdown = _shutdownStarted;
            if (rejectedDuringShutdown)
            {
                // Do not enqueue an activation that can never acquire a UI handler.
                handler = null;
            }
            else
            {
                handler = _redirectedActivationHandler;
                if (handler is null)
                {
                    if (PendingActivations.Count >= MaximumPendingActivations)
                    {
                        // External activation is navigation-only. Retaining the newest bounded
                        // set avoids an unbounded queue if the UI thread is delayed at startup.
                        PendingActivations.Dequeue();
                    }

                    PendingActivations.Enqueue(activation);
                    return;
                }
            }
        }

        if (rejectedDuringShutdown)
        {
            ShowFatalApplicationError(
                "Package Pilot is shutting down, so the redirected request was not opened. Start Package Pilot again and retry.");
            return;
        }

        handler!(activation);
    }

    private static RedirectResult RedirectActivationTo(
        AppActivationArguments activation,
        AppInstance registeredInstance)
    {
        var redirectCompleted = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset);
        var redirectTask = Task.Run(async () =>
        {
            try
            {
                await registeredInstance.RedirectActivationToAsync(activation);
                return (Exception?)null;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                return exception;
            }
            finally
            {
                redirectCompleted.Set();
            }
        });

        uint waitResult;
        try
        {
            waitResult = CoWaitForMultipleObjects(
                dwFlags: 0,
                dwMilliseconds: RedirectTimeoutMilliseconds,
                nHandles: 1,
                pHandles: [redirectCompleted.SafeWaitHandle.DangerousGetHandle()],
                out _);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _ = redirectTask.ContinueWith(
                _ => redirectCompleted.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return RedirectResult.Failed(
                $"Windows could not wait for activation redirection (0x{exception.HResult:X8}).");
        }

        if (waitResult != 0)
        {
            _ = redirectTask.ContinueWith(
                _ => redirectCompleted.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return RedirectResult.Failed(
                "The existing Package Pilot instance did not respond within five seconds.");
        }

        var redirectFailure = redirectTask.GetAwaiter().GetResult();
        redirectCompleted.Dispose();
        if (redirectFailure is not null)
        {
            return RedirectResult.Failed(
                $"Windows could not redirect activation (0x{redirectFailure.HResult:X8}).");
        }

        try
        {
            using Process process = Process.GetProcessById((int)registeredInstance.ProcessId);
            if (process.MainWindowHandle != 0)
            {
                _ = SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Redirection already succeeded. Foreground activation is best effort.
        }

        return RedirectResult.Success;
    }

    private static bool IsProcessRunning(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // If Windows will not let us inspect the owner, fail closed instead of
            // risking a second foreground service graph.
            return true;
        }
    }

    private static void ShowRedirectionFailure(RedirectResult result)
    {
        _ = MessageBox(
            nint.Zero,
            $"Package Pilot could not open this request safely. {result.Message}\n\nExit the existing Package Pilot process from its notification-area menu or Task Manager, then try again.",
            "Package Pilot",
            MessageBoxOk | MessageBoxIconError);
    }

    internal static void ShowFatalApplicationError(string message)
    {
        _ = MessageBox(
            nint.Zero,
            message,
            "Package Pilot",
            MessageBoxOk | MessageBoxIconError);
    }

    private static bool IsRecoverable(Exception exception) => exception is not
        OutOfMemoryException and not
        StackOverflowException and not
        AccessViolationException;

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags,
        uint dwMilliseconds,
        uint nHandles,
        nint[] pHandles,
        out uint dwIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        nint hWnd,
        string text,
        string caption,
        uint type);

    private sealed record RedirectResult(bool Succeeded, string Message)
    {
        internal static RedirectResult Success { get; } = new(true, string.Empty);

        internal static RedirectResult Failed(string message) => new(false, message);
    }
}
