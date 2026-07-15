using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using Windows.Storage;

namespace PackagePilot.Windows.Services;

/// <summary>Persists identity-scoped window and notification-area preferences.</summary>
public sealed class WindowsAppBehaviorSettingsStore : IAppBehaviorSettingsStore
{
    internal const string ShowNotificationAreaIconKey = "showNotificationAreaIcon";
    internal const string HideOnStartupTaskActivationKey = "hideOnStartupTaskActivation";
    internal const string MinimizeToNotificationAreaKey = "minimizeToNotificationArea";
    internal const string CloseToNotificationAreaKey = "closeToNotificationArea";

    private static readonly string[] Keys =
    [
        ShowNotificationAreaIconKey,
        HideOnStartupTaskActivationKey,
        MinimizeToNotificationAreaKey,
        CloseToNotificationAreaKey
    ];

    private readonly IDictionary<string, object> _values;

    public WindowsAppBehaviorSettingsStore(ApplicationDataContainer? settings = null)
        : this((settings ?? ApplicationData.Current.LocalSettings).Values)
    {
    }

    internal WindowsAppBehaviorSettingsStore(IDictionary<string, object> values)
    {
        _values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public ValueTask<AppBehaviorSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AppBehaviorSettings
        {
            ShowNotificationAreaIcon = ReadBoolean(ShowNotificationAreaIconKey),
            HideOnStartupTaskActivation = ReadBoolean(HideOnStartupTaskActivationKey),
            MinimizeToNotificationArea = ReadBoolean(MinimizeToNotificationAreaKey),
            CloseToNotificationArea = ReadBoolean(CloseToNotificationAreaKey)
        });
    }

    public ValueTask SaveAsync(
        AppBehaviorSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var previous = Keys.ToDictionary(
            key => key,
            key => _values.TryGetValue(key, out var value)
                ? new PreviousValue(true, value)
                : new PreviousValue(false, null),
            StringComparer.Ordinal);

        try
        {
            Write(ShowNotificationAreaIconKey, settings.ShowNotificationAreaIcon, cancellationToken);
            Write(HideOnStartupTaskActivationKey, settings.HideOnStartupTaskActivation, cancellationToken);
            Write(MinimizeToNotificationAreaKey, settings.MinimizeToNotificationArea, cancellationToken);
            Write(CloseToNotificationAreaKey, settings.CloseToNotificationArea, cancellationToken);
        }
        catch
        {
            Restore(previous);
            throw;
        }

        return ValueTask.CompletedTask;
    }

    private bool ReadBoolean(string key) =>
        _values.TryGetValue(key, out var value) && value is bool flag && flag;

    private void Write(string key, bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value;
    }

    private void Restore(IReadOnlyDictionary<string, PreviousValue> previous)
    {
        foreach (var item in previous)
        {
            try
            {
                if (item.Value.Existed)
                {
                    _values[item.Key] = item.Value.Value!;
                }
                else
                {
                    _values.Remove(item.Key);
                }
            }
            catch
            {
                // Preserve the original write failure. LocalSettings will be read again on
                // next launch, so a partially restored value cannot widen permissions.
            }
        }
    }

    private readonly record struct PreviousValue(bool Existed, object? Value);
}
