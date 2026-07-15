using System.Globalization;
using System.Security;
using Microsoft.Windows.AppNotifications;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Windows.Services;

/// <summary>Applies an already-evaluated notification decision to Windows.</summary>
public sealed class WindowsUpdateNotificationService : IUpdateNotificationSink
{
    private readonly AppNotificationManager _manager;

    public WindowsUpdateNotificationService(AppNotificationManager? manager = null)
    {
        _manager = manager ?? AppNotificationManager.Default;
    }

    public async Task ApplyAsync(
        UpdateNotificationDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();
        UpdateBadge(decision.BadgeCount);

        if (decision.ClearNotification)
        {
            await _manager.RemoveByTagAndGroupAsync(
                WindowsIntegrationConstants.NotificationTag,
                WindowsIntegrationConstants.NotificationGroup);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (!decision.ShowOrReplaceNotification)
        {
            return;
        }

        var count = decision.BadgeCount;
        var detail = count == 1
            ? "1 application update is ready for review."
            : $"{count.ToString(CultureInfo.CurrentCulture)} application updates are ready for review.";
        var payload = $"""
            <toast launch="packagepilot://updates">
              <visual>
                <binding template="ToastGeneric">
                  <text>Updates ready to review</text>
                  <text>{SecurityElement.Escape(detail)}</text>
                </binding>
              </visual>
            </toast>
            """;
        var notification = new AppNotification(payload)
        {
            Tag = WindowsIntegrationConstants.NotificationTag,
            Group = WindowsIntegrationConstants.NotificationGroup
        };
        _manager.Show(notification);
    }

    public static void SetBadgeCount(int count) => WindowsBadgeService.SetCount(count);

    public static void ClearBadge() => WindowsBadgeService.Clear();

    private static void UpdateBadge(int count) => WindowsBadgeService.SetCount(count);
}
