using System.Runtime.InteropServices;
using PackagePilot.Core.Models;

namespace PackagePilot.App.Services;

/// <summary>Mirrors the active package operation on the Windows taskbar button.</summary>
internal sealed class WindowsTaskbarProgressService : IDisposable
{
    private readonly nint _windowHandle;
    private readonly ITaskbarList3? _taskbar;

    internal WindowsTaskbarProgressService(Microsoft.UI.Xaml.Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        try
        {
            _taskbar = (ITaskbarList3)(object)new TaskbarList();
            _taskbar.HrInit();
        }
        catch (COMException)
        {
            _taskbar = null;
        }
    }

    internal void Update(OperationQueueEntry? current, int pendingCount)
    {
        if (_taskbar is null || _windowHandle == 0)
        {
            return;
        }

        try
        {
            if (current is null)
            {
                _taskbar.SetProgressState(
                    _windowHandle,
                    pendingCount > 0
                        ? TaskbarProgressState.Indeterminate
                        : TaskbarProgressState.NoProgress);
                return;
            }

            if (current.Progress.Percent is not { } percent
                || double.IsNaN(percent)
                || double.IsInfinity(percent))
            {
                _taskbar.SetProgressState(_windowHandle, TaskbarProgressState.Indeterminate);
                return;
            }

            var completed = (ulong)Math.Clamp(Math.Round(percent), 0, 100);
            _taskbar.SetProgressState(_windowHandle, TaskbarProgressState.Normal);
            _taskbar.SetProgressValue(_windowHandle, completed, 100);
        }
        catch (COMException)
        {
            // Explorer can restart while an operation is active. The in-app queue remains
            // authoritative and the taskbar indicator is best-effort shell integration.
        }
    }

    public void Dispose()
    {
        if (_taskbar is not null && Marshal.IsComObject(_taskbar))
        {
            Marshal.FinalReleaseComObject(_taskbar);
        }
    }

    private enum TaskbarProgressState : uint
    {
        NoProgress = 0,
        Indeterminate = 1,
        Normal = 2
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class TaskbarList
    {
    }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint window);
        void DeleteTab(nint window);
        void ActivateTab(nint window);
        void SetActiveAlt(nint window);
        void MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint window, ulong completed, ulong total);
        void SetProgressState(nint window, TaskbarProgressState state);
    }
}
