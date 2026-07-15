using System.Globalization;
using Windows.UI.Notifications;

namespace PackagePilot.Windows.Services;

/// <summary>Publishes the package-identity badge shared by foreground and background hosts.</summary>
public static class WindowsBadgeService
{
    public static void SetCount(int count)
    {
        var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
        if (count <= 0)
        {
            updater.Clear();
            return;
        }

        var document = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
        var badge = document.SelectSingleNode("/badge");
        if (badge?.Attributes?.GetNamedItem("value") is { } value)
        {
            value.NodeValue = Math.Min(count, 99).ToString(CultureInfo.InvariantCulture);
            updater.Update(new BadgeNotification(document));
        }
    }

    public static void Clear() => SetCount(0);
}
