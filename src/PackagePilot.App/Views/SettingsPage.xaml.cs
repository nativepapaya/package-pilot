using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using Windows.Storage;

namespace PackagePilot.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
    private bool _isLoading = true;

    public SettingsPage()
    {
        InitializeComponent();
        VersionText.Text = GetCurrentVersionLabel();
        LoadPreferences();
        _isLoading = false;
    }

    public ObservableCollection<SourceHealthItem> Sources { get; } = [];
    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public void SetCapabilitySummary(string summary) => CapabilityText.Text = summary;

    private void LoadPreferences()
    {
        SelectByTag(ThemeBox, ReadString("theme", "system"));
        SelectByTag(ScopeBox, ReadString("installScope", "default"));
        SelectByTag(ArchitectureBox, ReadString("architecture", "auto"));
        ReduceMotionToggle.IsOn = ReadBoolean("reduceMotion", false);
    }

    private string ReadString(string key, string fallback) => _settings.Values.TryGetValue(key, out var value) && value is string text ? text : fallback;
    private bool ReadBoolean(string key, bool fallback) => _settings.Values.TryGetValue(key, out var value) && value is bool flag ? flag : fallback;

    private static void SelectByTag(ComboBox box, string tag)
    {
        var match = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = match ?? box.Items[0];
    }

    private void SaveComboSetting(string key, ComboBox box)
    {
        if (_isLoading || box.SelectedItem is not ComboBoxItem { Tag: string value })
        {
            return;
        }

        _settings.Values[key] = value;
        SettingChanged?.Invoke(this, new SettingChangedEventArgs(key, value));
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e) => SaveComboSetting("theme", ThemeBox);
    private void OnScopeChanged(object sender, SelectionChangedEventArgs e) => SaveComboSetting("installScope", ScopeBox);
    private void OnArchitectureChanged(object sender, SelectionChangedEventArgs e) => SaveComboSetting("architecture", ArchitectureBox);

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        ShowUpdateStatus(
            "Checking for updates",
            "Contacting the public Package Pilot release feed…",
            InfoBarSeverity.Informational);

        try
        {
            var packageManager = new Windows.Management.Deployment.PackageManager();
            var currentPackage = packageManager.FindPackageForUser(string.Empty, Package.Current.Id.FullName);
            if (currentPackage is null)
            {
                ShowUpdateStatus(
                    "Update check unavailable",
                    "Windows could not find this Package Pilot installation. Use Get update installer to reconnect it to the release feed.",
                    InfoBarSeverity.Warning);
                return;
            }

            var result = await currentPackage.CheckUpdateAvailabilityAsync();
            switch (result.Availability)
            {
                case PackageUpdateAvailability.Available:
                    ShowUpdateStatus(
                        "Update available",
                        "Windows found a newer Package Pilot release. It will be applied by App Installer; use Get update installer to start the process now.",
                        InfoBarSeverity.Success);
                    break;
                case PackageUpdateAvailability.Required:
                    ShowUpdateStatus(
                        "Required update available",
                        "A required Package Pilot update is ready. Close the app and use Get update installer to apply it now.",
                        InfoBarSeverity.Warning);
                    break;
                case PackageUpdateAvailability.NoUpdates:
                    ShowUpdateStatus(
                        "Package Pilot is up to date",
                        "No newer release is available on the configured update feed.",
                        InfoBarSeverity.Success);
                    break;
                case PackageUpdateAvailability.Error:
                    var errorCode = result.ExtendedError?.HResult ?? unchecked((int)0x80004005);
                    ShowUpdateStatus(
                        "Update check failed",
                        $"Windows could not check the Package Pilot feed (0x{errorCode:X8}). Check the network and App Installer, then try again.",
                        InfoBarSeverity.Error);
                    break;
                case PackageUpdateAvailability.Unknown:
                default:
                    ShowUpdateStatus(
                        "Connect the update feed",
                        "This copy was not installed through PackagePilot.appinstaller, so Windows cannot check it automatically yet. Use Get update installer once to enable updates.",
                        InfoBarSeverity.Informational);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowUpdateStatus(
                "Update check failed",
                $"Windows could not check the Package Pilot feed (0x{ex.HResult:X8}). Check the network and App Installer, then try again.",
                InfoBarSeverity.Error);
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private static string GetCurrentVersionLabel()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"Version {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch
        {
            return "Development build";
        }
    }

    private void ShowUpdateStatus(string title, string message, InfoBarSeverity severity)
    {
        UpdateInfoBar.Title = title;
        UpdateInfoBar.Message = message;
        UpdateInfoBar.Severity = severity;
        UpdateInfoBar.IsOpen = true;

        if (FrameworkElementAutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            var peer = FrameworkElementAutomationPeer.FromElement(UpdateInfoBar)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(UpdateInfoBar);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void OnReduceMotionToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Values["reduceMotion"] = ReduceMotionToggle.IsOn;
        SettingChanged?.Invoke(this, new SettingChangedEventArgs("reduceMotion", ReduceMotionToggle.IsOn));
    }
}
