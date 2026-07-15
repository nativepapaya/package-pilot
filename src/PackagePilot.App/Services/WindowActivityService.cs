using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.App.Services;

/// <summary>
/// Maintains the foreground-window state used by notification suppression. The window owns
/// the authoritative visibility/activation signals and updates this UI-thread service.
/// </summary>
internal sealed class WindowActivityService : IWindowActivityService
{
    private const int VisibleFlag = 1;
    private const int ActiveFlag = 2;
    private int _stateFlags;

    public event EventHandler<WindowActivityChangedEventArgs>? ActivityChanged;

    public WindowActivityState Current
    {
        get
        {
            var flags = Volatile.Read(ref _stateFlags);
            return new WindowActivityState(
                IsVisible: (flags & VisibleFlag) != 0,
                IsActive: (flags & ActiveFlag) != 0);
        }
    }

    internal void Set(bool isVisible, bool isActive)
    {
        var next = new WindowActivityState(isVisible, isActive);
        var nextFlags = (isVisible ? VisibleFlag : 0) | (isActive ? ActiveFlag : 0);
        var previousFlags = Interlocked.Exchange(ref _stateFlags, nextFlags);
        if (previousFlags == nextFlags)
        {
            return;
        }

        ActivityChanged?.Invoke(this, new WindowActivityChangedEventArgs(next));
    }
}
