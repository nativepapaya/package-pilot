using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace PackagePilot.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
    private bool _isLoading = true;

    public SettingsPage()
    {
        InitializeComponent();
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
