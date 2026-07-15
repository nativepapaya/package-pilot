using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Coordinates user-requested resident-process behavior without exposing package mutation
/// capabilities to Settings or notification-area commands.
/// </summary>
public interface IAppLifetimeController
{
    AppBehaviorSettings BehaviorSettings { get; }

    Task<NotificationAreaIconResult> SetNotificationAreaEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<StartupRegistrationResult> GetStartupRegistrationAsync(
        CancellationToken cancellationToken = default);

    Task<StartupRegistrationResult> SetStartupEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    Task OpenWindowsStartupSettingsAsync(
        CancellationToken cancellationToken = default);
}
