using Microsoft.UI.Xaml;
using PackagePilot.App.ViewModels;
using Windows.Graphics;

namespace PackagePilot.App;

/// <summary>The Mica-backed application window and custom-title-bar host.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1180, 760));

        RootFrame.Content = new MainPage(viewModel);
    }
}
