using Windows.UI.StartScreen;

namespace PackagePilot.Windows.Services;

public sealed class WindowsJumpListService
{
    public async Task ConfigureAsync()
    {
        if (!JumpList.IsSupported())
        {
            return;
        }

        var jumpList = await JumpList.LoadCurrentAsync();
        jumpList.SystemGroupKind = JumpListSystemGroupKind.None;
        jumpList.Items.Clear();
        Add(jumpList, "updates", "Review updates", "Review application updates");
        Add(jumpList, "installed", "Installed apps", "Manage installed applications");
        Add(jumpList, "discover", "Discover", "Discover applications");
        Add(jumpList, "sources", "Sources", "Review package sources");
        Add(jumpList, "check", "Check now", "Check for application updates");
        await jumpList.SaveAsync();
    }

    private static void Add(JumpList jumpList, string arguments, string title, string description)
    {
        var item = JumpListItem.CreateWithArguments(arguments, title);
        item.Description = description;
        item.GroupName = "Package Pilot";
        jumpList.Items.Add(item);
    }
}
