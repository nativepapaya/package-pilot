using System.Collections.ObjectModel;
using PackagePilot.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PackagePilot.App.Services;
using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;
using PackagePilot.Core.Services;
using Windows.ApplicationModel;
using Windows.Storage;

namespace PackagePilot.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
    private readonly WindowsBackgroundUpdateRegistrationService _backgroundRegistration = new();
    private IAppLifetimeController? _appLifetimeController;
    private IAppLifetimeActivityGate? _lifetimeActivityGate;
    private bool _isLoading = true;
    private bool _backgroundStatusLoaded;
    private bool _appLifetimeMutationInProgress;
    private int _appLifetimeRefreshVersion;

    public SettingsPage()
    {
        InitializeComponent();
        VersionText.Text = GetCurrentVersionLabel();
        LoadPreferences();
        Loaded += OnLoaded;
        _isLoading = false;
    }

    public ObservableCollection<SourceHealthItem> Sources { get; } = [];
    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public void SetCapabilitySummary(string summary) => CapabilityText.Text = summary;

    internal void ConfigureAppLifetime(
        IAppLifetimeController? controller,
        IAppLifetimeActivityGate? lifetimeActivityGate)
    {
        _appLifetimeController = controller;
        _lifetimeActivityGate = lifetimeActivityGate;
        _ = RefreshAppLifetimeStateAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAppLifetimeStateAsync();
        if (_backgroundStatusLoaded)
        {
            return;
        }

        _backgroundStatusLoaded = true;
        await RefreshBackgroundMonitoringStatusAsync(showAlerts: true);
    }

    private async Task<BackgroundMonitoringStatus> RefreshBackgroundMonitoringStatusAsync(
        bool showAlerts)
    {
        var store = new JsonBackgroundUpdateRunStatusStore(Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "background-update-status.json"));
        var status = await store.LoadAsync();
        var monitoring = _backgroundRegistration.GetStatus(status);
        string schedule = monitoring.DesiredCadence == monitoring.ActualCadence
            ? $"Schedule: {DescribeCadence(monitoring.DesiredCadence)}."
            : $"Requested: {DescribeCadence(monitoring.DesiredCadence)}; Windows currently has {DescribeCadence(monitoring.ActualCadence)}.";
        string run = status is null
            ? "No background check has run yet."
            : status.State == BackgroundUpdateRunState.Completed
                ? $"Last background check {status.AttemptedAt.ToLocalTime():g}: {status.UpdateCount} update{(status.UpdateCount == 1 ? string.Empty : "s")} found."
                : $"Last background attempt {status.AttemptedAt.ToLocalTime():g}: {status.State}.";
        BackgroundStatusText.Text = $"{schedule} {run}";

        if (showAlerts && !monitoring.RegistrationHealthy)
        {
            BackgroundMonitoringInfoBar.Title = "Background schedule needs attention";
            BackgroundMonitoringInfoBar.Message = monitoring.Error
                ?? "Windows did not confirm the requested Package Pilot background schedule.";
            BackgroundMonitoringInfoBar.Severity = monitoring.RegistrationState
                == BackgroundMonitoringState.Denied
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Error;
            BackgroundMonitoringInfoBar.IsOpen = true;
        }
        else if (showAlerts && monitoring.ForegroundFallbackRequired)
        {
            BackgroundMonitoringInfoBar.Title = "Background WinGet access unavailable";
            BackgroundMonitoringInfoBar.Message = string.IsNullOrWhiteSpace(monitoring.Error)
                ? "Windows could not activate WinGet in the background host. Package Pilot will defer discovery until the foreground app runs."
                : $"{monitoring.Error} Package Pilot will defer discovery until the foreground app runs.";
            BackgroundMonitoringInfoBar.Severity = InfoBarSeverity.Warning;
            BackgroundMonitoringInfoBar.IsOpen = true;
        }

        return monitoring;
    }

    private static string DescribeCadence(UpdateMonitoringCadence cadence) => cadence switch
    {
        UpdateMonitoringCadence.Manual => "Manual",
        UpdateMonitoringCadence.EverySixHours => "Every 6 hours",
        _ => "Daily"
    };

    private void LoadPreferences()
    {
        SelectByTag(ThemeBox, ReadString("theme", "system"));
        SelectByTag(ScopeBox, ReadString("installScope", "default"));
        SelectByTag(ArchitectureBox, ReadString("architecture", "auto"));
        SelectByTag(UpdateCadenceBox, ReadString("updateMonitoringCadence", "daily"));
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

    private async void OnNotificationAreaToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _appLifetimeController is null)
        {
            return;
        }

        _appLifetimeMutationInProgress = true;
        _appLifetimeRefreshVersion++;
        SetAppLifetimeControlsEnabled(false);
        try
        {
            bool enable = NotificationAreaToggle.IsOn;
            var result = await _appLifetimeController.SetNotificationAreaEnabledAsync(enable);
            bool succeeded = enable ? result.IsVisible : result.State == NotificationAreaIconState.Hidden;
            if (!succeeded)
            {
                ShowAppLifetimeStatus(
                    "Notification-area mode unavailable",
                    result.Message ?? "Windows could not apply this setting.",
                    InfoBarSeverity.Error);
            }
            else
            {
                ShowAppLifetimeStatus(
                    enable ? "Notification-area mode enabled" : "Notification-area mode disabled",
                    enable
                        ? "Close now hides Package Pilot. Minimize continues to use the taskbar, and Exit Package Pilot fully quits."
                        : "Close now exits Package Pilot normally. Start with Windows is also off.",
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowAppLifetimeStatus(
                "Notification-area setting failed",
                $"Package Pilot could not apply the setting (0x{exception.HResult:X8}).",
                InfoBarSeverity.Error);
        }
        finally
        {
            _appLifetimeMutationInProgress = false;
            await RefreshAppLifetimeStateAsync();
        }
    }

    private async void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _appLifetimeController is null)
        {
            return;
        }

        _appLifetimeMutationInProgress = true;
        _appLifetimeRefreshVersion++;
        SetAppLifetimeControlsEnabled(false);
        try
        {
            var result = await _appLifetimeController.SetStartupEnabledAsync(
                StartWithWindowsToggle.IsOn);
            ShowStartupRegistrationStatus(result);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowAppLifetimeStatus(
                "Start with Windows failed",
                $"Package Pilot could not change Windows startup registration (0x{exception.HResult:X8}).",
                InfoBarSeverity.Error);
        }
        finally
        {
            _appLifetimeMutationInProgress = false;
            await RefreshAppLifetimeStateAsync();
        }
    }

    private async void OnOpenStartupAppsSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_appLifetimeController is null)
        {
            return;
        }

        try
        {
            await _appLifetimeController.OpenWindowsStartupSettingsAsync();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ShowAppLifetimeStatus(
                "Windows Startup apps could not open",
                $"Open Settings > Apps > Startup manually (0x{exception.HResult:X8}).",
                InfoBarSeverity.Error);
        }
    }

    private async Task RefreshAppLifetimeStateAsync()
    {
        int refreshVersion = ++_appLifetimeRefreshVersion;
        SetAppLifetimeControlsEnabled(false);
        if (_appLifetimeController is null)
        {
            if (refreshVersion != _appLifetimeRefreshVersion
                || _appLifetimeMutationInProgress)
            {
                return;
            }

            _isLoading = true;
            NotificationAreaToggle.IsOn = false;
            StartWithWindowsToggle.IsOn = false;
            _isLoading = false;
            NotificationAreaToggle.IsEnabled = false;
            StartWithWindowsToggle.IsEnabled = false;
            StartupSettingsButton.Visibility = Visibility.Collapsed;
            return;
        }

        StartupRegistrationResult startup;
        try
        {
            startup = await _appLifetimeController.GetStartupRegistrationAsync();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            startup = new StartupRegistrationResult
            {
                State = StartupRegistrationState.Failed,
                Message = $"Windows startup state is unavailable (0x{exception.HResult:X8})."
            };
        }

        if (refreshVersion != _appLifetimeRefreshVersion
            || _appLifetimeMutationInProgress)
        {
            return;
        }

        bool notificationAreaEnabled =
            _appLifetimeController.BehaviorSettings.UsesNotificationAreaIcon;
        _isLoading = true;
        try
        {
            NotificationAreaToggle.IsOn = notificationAreaEnabled;
            StartWithWindowsToggle.IsOn = startup.IsEnabled;
        }
        finally
        {
            _isLoading = false;
        }

        NotificationAreaToggle.IsEnabled = startup.State != StartupRegistrationState.EnabledByPolicy;
        StartWithWindowsToggle.IsEnabled = notificationAreaEnabled
            && startup.State is StartupRegistrationState.Disabled or StartupRegistrationState.Enabled;
        StartupSettingsButton.Visibility = startup.RequiresWindowsSettings
            ? Visibility.Visible
            : Visibility.Collapsed;
        StartupSettingsButton.IsEnabled = true;
        StartupStateText.Text = startup.Message ?? "Windows startup state is unavailable.";
    }

    private void SetAppLifetimeControlsEnabled(bool enabled)
    {
        NotificationAreaToggle.IsEnabled = enabled;
        StartWithWindowsToggle.IsEnabled = enabled && NotificationAreaToggle.IsOn;
        StartupSettingsButton.IsEnabled = enabled;
    }

    private void ShowStartupRegistrationStatus(StartupRegistrationResult result)
    {
        var severity = result.State switch
        {
            StartupRegistrationState.Enabled or StartupRegistrationState.Disabled =>
                InfoBarSeverity.Success,
            StartupRegistrationState.DisabledByUser
                or StartupRegistrationState.DisabledByPolicy
                or StartupRegistrationState.EnabledByPolicy => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Error
        };
        ShowAppLifetimeStatus(
            result.IsEnabled ? "Start with Windows enabled" : "Start with Windows not enabled",
            result.Message ?? "Windows returned no startup-registration detail.",
            severity);
    }

    private void ShowAppLifetimeStatus(string title, string message, InfoBarSeverity severity)
    {
        AppLifetimeInfoBar.Title = title;
        AppLifetimeInfoBar.Message = message;
        AppLifetimeInfoBar.Severity = severity;
        AppLifetimeInfoBar.IsOpen = true;
    }

    private async void OnUpdateCadenceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || UpdateCadenceBox.SelectedItem is not ComboBoxItem { Tag: string value })
        {
            return;
        }

        using var activity = _lifetimeActivityGate?.TryEnter(
            AppLifetimeActivityKind.WindowsIntegration);
        if (_lifetimeActivityGate is not null && activity is null)
        {
            _isLoading = true;
            try
            {
                SelectByTag(
                    UpdateCadenceBox,
                    ReadString("updateMonitoringCadence", "daily"));
            }
            finally
            {
                _isLoading = false;
            }

            BackgroundMonitoringInfoBar.Title = "Finish the current operation first";
            BackgroundMonitoringInfoBar.Message =
                "Package Pilot did not change the background schedule while another source or Windows integration operation was active.";
            BackgroundMonitoringInfoBar.Severity = InfoBarSeverity.Informational;
            BackgroundMonitoringInfoBar.IsOpen = true;
            return;
        }

        _settings.Values["updateMonitoringCadence"] = value;
        SettingChanged?.Invoke(this, new SettingChangedEventArgs("updateMonitoringCadence", value));

        var cadence = value switch
        {
            "manual" => UpdateMonitoringCadence.Manual,
            "sixHours" => UpdateMonitoringCadence.EverySixHours,
            _ => UpdateMonitoringCadence.Daily
        };
        UpdateCadenceBox.IsEnabled = false;
        try
        {
            var result = await _backgroundRegistration.ConfigureAsync(cadence);
            var monitoring = await RefreshBackgroundMonitoringStatusAsync(showAlerts: false);
            SettingChanged?.Invoke(
                this,
                new SettingChangedEventArgs(
                    "backgroundMonitoringState",
                    monitoring.RegistrationState));
            BackgroundMonitoringInfoBar.Title = result.State switch
            {
                BackgroundMonitoringState.Registered => "Background monitoring enabled",
                BackgroundMonitoringState.Disabled => "Manual checks only",
                BackgroundMonitoringState.Denied => "Background access denied",
                _ => "Background monitoring unavailable"
            };
            BackgroundMonitoringInfoBar.Message = result.Message ?? string.Empty;
            BackgroundMonitoringInfoBar.Severity = result.State switch
            {
                BackgroundMonitoringState.Registered or BackgroundMonitoringState.Disabled => InfoBarSeverity.Success,
                BackgroundMonitoringState.Denied => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Error
            };
            BackgroundMonitoringInfoBar.IsOpen = true;
        }
        finally
        {
            UpdateCadenceBox.IsEnabled = true;
        }
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        ShowUpdateStatus(
            "Checking for updates",
            "Contacting the public Package Pilot release feed…",
            InfoBarSeverity.Informational);

        try
        {
            var packageManager = new global::Windows.Management.Deployment.PackageManager();
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

    private static bool IsRecoverable(Exception exception) => exception is not
        OperationCanceledException and not
        OutOfMemoryException and not
        StackOverflowException and not
        AccessViolationException;
}
