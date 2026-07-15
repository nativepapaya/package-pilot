using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Owns Package Pilot's opt-in notification-area icon through a message-only HWND.
/// Construct, call, and dispose this service on the same thread (normally the WinUI thread).
/// </summary>
public sealed class WindowsNotificationAreaIconService : INotificationAreaIconService
{
    private const uint WmNull = 0x0000;
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmContextMenu = 0x007B;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmUser = 0x0400;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint WmApp = 0x8000;
    private const uint CallbackMessage = WmApp + 0x44;
    private const uint RestoreIconMessage = WmApp + 0x45;
    private const uint NinSelect = WmUser;
    private const uint NinKeySelect = WmUser + 1;
    private const nint WtsSessionLogoff = 0x6;
    private const uint NotifyForThisSession = 0;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifGuid = 0x00000020;
    private const uint NifShowTip = 0x00000080;

    private const uint ImageIcon = 1;
    private const int SmCxSmallIcon = 49;
    private const int SmCySmallIcon = 50;
    private const uint LrDefaultSize = 0x00000040;
    private const uint LrLoadFromFile = 0x00000010;
    private const nint IdiApplication = 32512;
    private static readonly nint HwndMessage = new(-3);

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint TpmNoNotify = 0x0080;
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private const uint MenuOpen = 1001;
    private const uint MenuReviewUpdates = 1002;
    private const uint MenuCheckNow = 1003;
    private const uint MenuSettings = 1004;
    private const uint MenuExit = 1005;

    private const int MaximumToolTipLength = 127;
    private const string TaskbarCreatedMessageName = "TaskbarCreated";

    private static readonly ConcurrentDictionary<nint, WeakReference<WindowsNotificationAreaIconService>>
        Instances = new();
    private static readonly WindowProcedure SharedWindowProcedure = DispatchWindowMessage;

    private readonly uint _ownerThreadId;
    private readonly nint _moduleHandle;
    private readonly string _windowClassName;
    private readonly uint _taskbarCreatedMessage;
    private ushort _windowClassAtom;
    private nint _windowHandle;
    private nint _broadcastWindowHandle;
    private nint _iconHandle;
    private bool _ownsIcon;
    private bool _sessionNotificationRegistered;
    private bool _isVisible;
    private bool _shutdownSignaled;
    private bool _disposed;
    private NotificationAreaIconOptions _options = new();

    public WindowsNotificationAreaIconService(string? iconPath = null)
    {
        _ownerThreadId = GetCurrentThreadId();
        _moduleHandle = GetModuleHandleW(null);
        if (_moduleHandle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _windowClassName = $"PackagePilot.NotificationArea.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _taskbarCreatedMessage = RegisterWindowMessageW(TaskbarCreatedMessageName);
        if (_taskbarCreatedMessage == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Instance = _moduleHandle,
            WindowProcedure = SharedWindowProcedure,
            ClassName = _windowClassName
        };
        _windowClassAtom = RegisterClassExW(ref windowClass);
        if (_windowClassAtom == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _windowHandle = CreateWindowExW(
            0,
            _windowClassName,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            0,
            _moduleHandle,
            0);
        if (_windowHandle == 0)
        {
            int error = Marshal.GetLastWin32Error();
            _ = UnregisterClassW(_windowClassName, _moduleHandle);
            _windowClassAtom = 0;
            throw new Win32Exception(error);
        }

        Instances[_windowHandle] = new WeakReference<WindowsNotificationAreaIconService>(this);

        // HWND_MESSAGE does not receive broadcast messages. Keep the tray callback on its
        // message-only HWND, and use this never-shown tool window solely for TaskbarCreated
        // recovery and session-ending broadcasts.
        _broadcastWindowHandle = CreateWindowExW(
            WsExToolWindow | WsExNoActivate,
            _windowClassName,
            string.Empty,
            WsPopup,
            0,
            0,
            0,
            0,
            0,
            0,
            _moduleHandle,
            0);
        if (_broadcastWindowHandle == 0)
        {
            int error = Marshal.GetLastWin32Error();
            Instances.TryRemove(_windowHandle, out _);
            _ = DestroyWindow(_windowHandle);
            _windowHandle = 0;
            _ = UnregisterClassW(_windowClassName, _moduleHandle);
            _windowClassAtom = 0;
            throw new Win32Exception(error);
        }

        Instances[_broadcastWindowHandle] = new WeakReference<WindowsNotificationAreaIconService>(this);
        _sessionNotificationRegistered = WTSRegisterSessionNotification(
            _broadcastWindowHandle,
            NotifyForThisSession);
        LoadIcon(iconPath);
    }

    public event EventHandler<NotificationAreaActionRequestedEventArgs>? ActionRequested;
    public event EventHandler<NotificationAreaAvailabilityChangedEventArgs>? AvailabilityChanged;
    public event EventHandler? MenuOpening;
    public event EventHandler? ShutdownRequested;

    public bool IsVisible => _isVisible;

    public NotificationAreaIconResult Show(NotificationAreaIconOptions? options = null)
    {
        VerifyAccess();
        ThrowIfDisposed();
        if (options is not null)
        {
            _options = Normalize(options);
        }

        if (_isVisible)
        {
            return Update(_options);
        }

        return AddIcon();
    }

    public NotificationAreaIconResult Update(NotificationAreaIconOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        VerifyAccess();
        ThrowIfDisposed();
        _options = Normalize(options);

        if (!_isVisible)
        {
            return Hidden("The notification-area icon is not visible.");
        }

        var data = CreateIconData();
        if (!ShellNotifyIconW(NimModify, ref data))
        {
            _isVisible = false;
            var failure = NativeFailure("Windows could not update the notification-area icon.");
            RaiseAvailabilityChanged(failure, NotificationAreaAvailabilityReason.UpdateFailed);
            return failure;
        }

        return Visible();
    }

    public NotificationAreaIconResult Hide()
    {
        VerifyAccess();
        ThrowIfDisposed();
        if (!_isVisible)
        {
            return Hidden();
        }

        var data = CreateIconData();
        if (!ShellNotifyIconW(NimDelete, ref data))
        {
            return NativeFailure("Windows could not remove the notification-area icon.");
        }

        _isVisible = false;
        return Hidden();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        VerifyAccess();
        _disposed = true;

        if (_isVisible)
        {
            var data = CreateIconData();
            _ = ShellNotifyIconW(NimDelete, ref data);
            _isVisible = false;
        }

        if (_windowHandle != 0)
        {
            if (_sessionNotificationRegistered)
            {
                _ = WTSUnRegisterSessionNotification(_broadcastWindowHandle);
                _sessionNotificationRegistered = false;
            }

            if (_broadcastWindowHandle != 0)
            {
                Instances.TryRemove(_broadcastWindowHandle, out _);
                _ = DestroyWindow(_broadcastWindowHandle);
                _broadcastWindowHandle = 0;
            }

            Instances.TryRemove(_windowHandle, out _);
            _ = DestroyWindow(_windowHandle);
            _windowHandle = 0;
        }

        if (_windowClassAtom != 0)
        {
            _ = UnregisterClassW(_windowClassName, _moduleHandle);
            _windowClassAtom = 0;
        }

        if (_ownsIcon && _iconHandle != 0)
        {
            _ = DestroyIcon(_iconHandle);
        }

        _iconHandle = 0;
        _ownsIcon = false;
        GC.SuppressFinalize(this);
    }

    internal nint ProcessWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            bool restore = _isVisible;
            _isVisible = false;
            if (restore && !_disposed)
            {
                var result = AddIcon();
                if (result.IsVisible)
                {
                    RaiseAvailabilityChanged(
                        result,
                        NotificationAreaAvailabilityReason.ExplorerRestartRecovered);
                }
                else if (!PostMessageW(_broadcastWindowHandle, RestoreIconMessage, 0, 0))
                {
                    RaiseAvailabilityChanged(
                        result,
                        NotificationAreaAvailabilityReason.ExplorerRestartFailed);
                }
            }

            return 0;
        }

        switch (message)
        {
            case CallbackMessage:
                HandleIconCallback(lParam);
                return 0;
            case RestoreIconMessage:
            {
                var result = AddIcon();
                RaiseAvailabilityChanged(
                    result,
                    result.IsVisible
                        ? NotificationAreaAvailabilityReason.ExplorerRestartRecovered
                        : NotificationAreaAvailabilityReason.ExplorerRestartFailed);
                return 0;
            }
            case WmQueryEndSession:
                SignalShutdown();
                return 1;
            case WmEndSession:
                if (wParam == 0)
                {
                    _shutdownSignaled = false;
                }
                else
                {
                    SignalShutdown();
                }

                return 0;
            case WmWtsSessionChange when wParam == WtsSessionLogoff:
                // Message-only windows do not receive every broadcast session message.
                // Explicit WTS registration provides a second sign-out/shutdown signal.
                SignalShutdown();
                return 0;
            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    internal static NotificationAreaAction? MapMenuCommand(uint command) => command switch
    {
        MenuOpen => NotificationAreaAction.Open,
        MenuReviewUpdates => NotificationAreaAction.ReviewUpdates,
        MenuCheckNow => NotificationAreaAction.CheckNow,
        MenuSettings => NotificationAreaAction.OpenSettings,
        MenuExit => NotificationAreaAction.Exit,
        _ => null
    };

    internal static string NormalizeToolTip(string? toolTip)
    {
        var normalized = string.IsNullOrWhiteSpace(toolTip)
            ? "Package Pilot"
            : toolTip.Replace('\0', ' ').Trim();
        return normalized.Length <= MaximumToolTipLength
            ? normalized
            : normalized[..MaximumToolTipLength];
    }

    private NotificationAreaIconResult AddIcon()
    {
        if (_windowHandle == 0 || _iconHandle == 0)
        {
            return new NotificationAreaIconResult
            {
                State = NotificationAreaIconState.Unavailable,
                Message = "The Windows notification-area icon resources are unavailable."
            };
        }

        var data = CreateIconData();
        if (!ShellNotifyIconW(NimAdd, ref data))
        {
            return NativeFailure("Windows could not add the notification-area icon.");
        }

        data.VersionOrTimeout = NotifyIconVersion4;
        if (!ShellNotifyIconW(NimSetVersion, ref data))
        {
            int error = Marshal.GetLastWin32Error();
            _ = ShellNotifyIconW(NimDelete, ref data);
            return NativeFailure(
                "Windows could not enable notification-area accessibility behavior.",
                error);
        }

        _isVisible = true;
        return Visible();
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip | NifGuid | NifShowTip,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        ToolTip = BuildToolTip(_options),
        Info = string.Empty,
        InfoTitle = string.Empty,
        ItemGuid = WindowsIntegrationConstants.NotificationAreaIconId
    };

    private void HandleIconCallback(nint lParam)
    {
        uint eventCode = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
        switch (eventCode)
        {
            case NinSelect:
            case NinKeySelect:
            case WmLButtonUp:
            case WmLButtonDoubleClick:
                RaiseAction(NotificationAreaAction.Open);
                break;
            case WmContextMenu:
            case WmRButtonUp:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        RaiseMenuOpening();
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            _ = AppendMenuW(menu, MfString, MenuOpen, "Open Package Pilot");
            _ = AppendMenuW(menu, MfString, MenuReviewUpdates, BuildReviewUpdatesLabel(_options.UpdateCount));
            _ = AppendMenuW(menu, MfString, MenuCheckNow, "Check now");
            _ = AppendMenuW(menu, MfString, MenuSettings, "Settings");
            _ = AppendMenuW(menu, MfSeparator, 0, null);
            _ = AppendMenuW(menu, MfString, MenuExit, "Exit Package Pilot");
            _ = SetMenuDefaultItem(menu, MenuOpen, false);

            if (!GetCursorPos(out var cursor))
            {
                cursor = default;
            }

            nint menuOwner = _broadcastWindowHandle != 0
                ? _broadcastWindowHandle
                : _windowHandle;
            _ = SetForegroundWindow(menuOwner);
            uint command = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmReturnCommand | TpmNoNotify,
                cursor.X,
                cursor.Y,
                menuOwner,
                0);
            _ = PostMessageW(menuOwner, WmNull, 0, 0);

            if (MapMenuCommand(command) is { } action)
            {
                RaiseAction(action);
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private void RaiseAction(NotificationAreaAction action)
    {
        var handlers = ActionRequested;
        if (handlers is null)
        {
            return;
        }

        var args = new NotificationAreaActionRequestedEventArgs(action);
        foreach (EventHandler<NotificationAreaActionRequestedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Never allow a managed subscriber exception to cross the native WndProc boundary.
            }
        }
    }

    private void RaiseMenuOpening()
    {
        var handlers = MenuOpening;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Never allow a cache refresh failure to cross the native WndProc boundary.
            }
        }
    }

    private void RaiseAvailabilityChanged(
        NotificationAreaIconResult result,
        NotificationAreaAvailabilityReason reason)
    {
        var handlers = AvailabilityChanged;
        if (handlers is null)
        {
            return;
        }

        var args = new NotificationAreaAvailabilityChangedEventArgs(result, reason);
        foreach (EventHandler<NotificationAreaAvailabilityChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Never allow a managed subscriber exception to cross the native WndProc boundary.
            }
        }
    }

    private void SignalShutdown()
    {
        if (_shutdownSignaled)
        {
            return;
        }

        _shutdownSignaled = true;
        var handlers = ShutdownRequested;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Shutdown must continue even if a consumer cannot update its close policy.
            }
        }
    }

    private void LoadIcon(string? iconPath)
    {
        string resolvedPath = string.IsNullOrWhiteSpace(iconPath)
            ? Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")
            : Path.GetFullPath(iconPath);

        if (File.Exists(resolvedPath))
        {
            uint dpi = GetDpiForWindow(_broadcastWindowHandle);
            if (dpi == 0)
            {
                dpi = GetDpiForSystem();
            }

            int width = Math.Max(16, GetSystemMetricsForDpi(SmCxSmallIcon, dpi));
            int height = Math.Max(16, GetSystemMetricsForDpi(SmCySmallIcon, dpi));
            _iconHandle = LoadImageW(
                0,
                resolvedPath,
                ImageIcon,
                width,
                height,
                LrLoadFromFile);
            _ownsIcon = _iconHandle != 0;
        }

        if (_iconHandle == 0)
        {
            _iconHandle = LoadIconW(0, IdiApplication);
            _ownsIcon = false;
        }
    }

    private static NotificationAreaIconOptions Normalize(NotificationAreaIconOptions options) =>
        options with
        {
            ToolTip = NormalizeToolTip(options.ToolTip),
            UpdateCount = options.UpdateCount is { } count
                ? Math.Clamp(count, 0, 9_999)
                : null
        };

    internal static string BuildReviewUpdatesLabel(int? updateCount) => updateCount is { } count
        ? $"Review updates ({Math.Clamp(count, 0, 9_999).ToString(CultureInfo.CurrentCulture)})"
        : "Review updates";

    internal static string BuildToolTip(NotificationAreaIconOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string baseText = NormalizeToolTip(options.ToolTip);
        if (options.UpdateCount is not { } count)
        {
            return baseText;
        }

        int normalizedCount = Math.Clamp(count, 0, 9_999);
        string updateText = normalizedCount == 1
            ? "1 update ready"
            : $"{normalizedCount.ToString(CultureInfo.CurrentCulture)} updates ready";
        return NormalizeToolTip($"{baseText} - {updateText}");
    }

    private static NotificationAreaIconResult Visible() => new()
    {
        State = NotificationAreaIconState.Visible
    };

    private static NotificationAreaIconResult Hidden(string? message = null) => new()
    {
        State = NotificationAreaIconState.Hidden,
        Message = message
    };

    private static NotificationAreaIconResult NativeFailure(string message, int? error = null) => new()
    {
        State = NotificationAreaIconState.Failed,
        Message = message,
        NativeErrorCode = error ?? Marshal.GetLastWin32Error()
    };

    private void VerifyAccess()
    {
        if (GetCurrentThreadId() != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "The notification-area icon must be used from the thread that created it.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static bool IsRecoverable(Exception exception) => exception is not
        OutOfMemoryException and not
        StackOverflowException and not
        AccessViolationException;

    private static nint DispatchWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (Instances.TryGetValue(window, out var weak) && weak.TryGetTarget(out var service))
        {
            nint result = service.ProcessWindowMessage(window, message, wParam, lParam);
            if (message == WmNcDestroy)
            {
                Instances.TryRemove(window, out _);
            }

            return result;
        }

        if (message == WmNcDestroy)
        {
            Instances.TryRemove(window, out _);
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WindowProcedure? WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string? ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ToolTip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(
        nint instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, uint item, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(
        nint menu,
        uint flags,
        int x,
        int y,
        nint window,
        nint parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint window, uint flags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(nint window);
}
