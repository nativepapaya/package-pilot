using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace PackagePilot.App.Views;

public sealed partial class PackageDetailsPage : Page
{
    public PackageDetailsPage() => InitializeComponent();

    public PackageListItem? Package { get; private set; }
    public event EventHandler<PackageActionRequestedEventArgs>? PackageActionRequested;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PackageListItem package)
        {
            Package = package;
            DetailsPane.ShowPackage(package);
        }
    }

    private void OnPrimaryActionInvoked(object? sender, EventArgs e)
    {
        if (Package is not null)
        {
            PackageActionRequested?.Invoke(this, new PackageActionRequestedEventArgs(Package));
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();
    private void OnCloseRequested(object? sender, EventArgs e) => GoBack();

    private void GoBack()
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }
}
