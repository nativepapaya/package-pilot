using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Owns an optional notification-area icon. Implementations may raise actions from their
/// native window thread; UI consumers should marshal when their implementation requires it.
/// </summary>
public interface INotificationAreaIconService : IDisposable
{
    event EventHandler<NotificationAreaActionRequestedEventArgs>? ActionRequested;
    /// <summary>
    /// Raised when an icon that was believed visible is unexpectedly lost or restored.
    /// Hide and Dispose do not raise this event.
    /// </summary>
    event EventHandler<NotificationAreaAvailabilityChangedEventArgs>? AvailabilityChanged;
    /// <summary>
    /// Raised immediately before the native context menu is built so a consumer can refresh
    /// cached, externally-produced state without polling.
    /// </summary>
    event EventHandler? MenuOpening;
    /// <summary>
    /// Raised when Windows begins sign-out or shutdown. Consumers must allow real window
    /// closure instead of applying close-to-notification-area behavior.
    /// </summary>
    event EventHandler? ShutdownRequested;

    bool IsVisible { get; }

    NotificationAreaIconResult Show(NotificationAreaIconOptions? options = null);
    NotificationAreaIconResult Update(NotificationAreaIconOptions options);
    NotificationAreaIconResult Hide();
}
