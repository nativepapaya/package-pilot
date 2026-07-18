using Microsoft.UI.Dispatching;

namespace PackagePilot.App.ViewModels;

[Flags]
internal enum DestinationChangeFlags
{
    None = 0,
    Discover = 1 << 0,
    Installed = 1 << 1,
    Updates = 1 << 2,
    Activity = 1 << 3,
    Sources = 1 << 4,
    Settings = 1 << 5,
    Shell = 1 << 6,
    Destinations = Discover | Installed | Updates | Activity | Sources | Settings,
    All = Destinations | Shell
}

/// <summary>
/// Coalesces model notifications into destination-scoped state and render passes. Hidden
/// destinations receive immutable state updates at low priority without touching cached XAML;
/// only the active destination and shell are rendered.
/// </summary>
internal sealed class DestinationUiStateCoordinator
{
    private readonly Func<DispatcherQueuePriority, DispatcherQueueHandler, bool> _enqueue;
    private readonly Action<DestinationChangeFlags> _updateState;
    private readonly Action<DestinationChangeFlags> _render;
    private readonly object _gate = new();
    private DestinationChangeFlags _pending;
    private DestinationChangeFlags _activeDestination = DestinationChangeFlags.Discover;
    private bool _scheduled;
    private bool _disposed;

    public DestinationUiStateCoordinator(
        DispatcherQueue dispatcher,
        Action<DestinationChangeFlags> apply)
        : this(
            (priority, callback) => dispatcher.TryEnqueue(priority, callback),
            _ => { },
            apply)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
    }

    internal DestinationUiStateCoordinator(
        Func<DispatcherQueuePriority, DispatcherQueueHandler, bool> enqueue,
        Action<DestinationChangeFlags> apply)
        : this(enqueue, _ => { }, apply)
    {
    }

    public DestinationUiStateCoordinator(
        DispatcherQueue dispatcher,
        Action<DestinationChangeFlags> updateState,
        Action<DestinationChangeFlags> render)
        : this(
            (priority, callback) => dispatcher.TryEnqueue(priority, callback),
            updateState,
            render)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
    }

    internal DestinationUiStateCoordinator(
        Func<DispatcherQueuePriority, DispatcherQueueHandler, bool> enqueue,
        Action<DestinationChangeFlags> updateState,
        Action<DestinationChangeFlags> render)
    {
        _enqueue = enqueue ?? throw new ArgumentNullException(nameof(enqueue));
        _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));
        _render = render ?? throw new ArgumentNullException(nameof(render));
    }

    public DestinationChangeFlags ActiveDestination
    {
        get
        {
            lock (_gate)
            {
                return _activeDestination;
            }
        }
    }

    public void Activate(DestinationChangeFlags destination)
    {
        if (!IsSingleDestination(destination))
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _activeDestination = destination;
            _pending |= destination | DestinationChangeFlags.Shell;
        }

        Schedule(DispatcherQueuePriority.Normal);
    }

    public void Invalidate(DestinationChangeFlags flags)
    {
        if (flags == DestinationChangeFlags.None)
        {
            return;
        }

        DispatcherQueuePriority priority;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending |= flags;
            priority = (flags & (_activeDestination | DestinationChangeFlags.Shell)) != 0
                ? DispatcherQueuePriority.Normal
                : DispatcherQueuePriority.Low;
        }

        Schedule(priority);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = DestinationChangeFlags.None;
        }
    }

    private void Schedule(DispatcherQueuePriority priority)
    {
        lock (_gate)
        {
            if (_disposed || _scheduled)
            {
                return;
            }

            _scheduled = true;
        }

        if (!_enqueue(priority, Drain))
        {
            lock (_gate)
            {
                _scheduled = false;
            }
        }
    }

    private void Drain()
    {
        DestinationChangeFlags pending;
        DestinationChangeFlags eligible;
        lock (_gate)
        {
            _scheduled = false;
            if (_disposed)
            {
                return;
            }

            pending = _pending;
            _pending = DestinationChangeFlags.None;
            eligible = pending & (_activeDestination | DestinationChangeFlags.Shell);
        }

        var destinationChanges = pending & DestinationChangeFlags.Destinations;
        if (destinationChanges != DestinationChangeFlags.None)
        {
            _updateState(destinationChanges);
        }

        if (eligible != DestinationChangeFlags.None)
        {
            _render(eligible);
        }
    }

    private static bool IsSingleDestination(DestinationChangeFlags value) =>
        value is DestinationChangeFlags.Discover
            or DestinationChangeFlags.Installed
            or DestinationChangeFlags.Updates
            or DestinationChangeFlags.Activity
            or DestinationChangeFlags.Sources
            or DestinationChangeFlags.Settings;
}
