using System.Globalization;
using System.Security;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PackagePilot.Background;

internal sealed class WindowsNotificationSink : IUpdateNotificationSink
{
    public Task ApplyAsync(
        UpdateNotificationDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsBadgeService.SetCount(decision.BadgeCount);

        if (decision.ClearNotification)
        {
            ToastNotificationManager.History.Remove(
                WindowsIntegrationConstants.NotificationTag,
                WindowsIntegrationConstants.NotificationGroup);
            return Task.CompletedTask;
        }

        if (!decision.ShowOrReplaceNotification)
        {
            return Task.CompletedTask;
        }

        var count = decision.BadgeCount;
        var detail = count == 1
            ? "1 application update is ready for review."
            : $"{count.ToString(CultureInfo.CurrentCulture)} application updates are ready for review.";
        var payload = $"""
            <toast activationType="protocol" launch="packagepilot://updates">
              <visual>
                <binding template="ToastGeneric">
                  <text>Updates ready to review</text>
                  <text>{SecurityElement.Escape(detail)}</text>
                </binding>
              </visual>
            </toast>
            """;
        var document = new XmlDocument();
        document.LoadXml(payload);
        var notification = new ToastNotification(document)
        {
            Tag = WindowsIntegrationConstants.NotificationTag,
            Group = WindowsIntegrationConstants.NotificationGroup
        };

        ToastNotificationManager.History.Remove(
            WindowsIntegrationConstants.NotificationTag,
            WindowsIntegrationConstants.NotificationGroup);
        ToastNotificationManager.CreateToastNotifier().Show(notification);
        return Task.CompletedTask;
    }
}
