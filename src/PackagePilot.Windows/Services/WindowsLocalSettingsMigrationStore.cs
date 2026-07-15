using PackagePilot.Core.Abstractions;
using Windows.Storage;

namespace PackagePilot.Windows.Services;

/// <summary>Adapts identity-scoped Windows LocalSettings for the neutral migration service.</summary>
public sealed class WindowsLocalSettingsMigrationStore : IIdentityMigrationSettingsStore
{
    private readonly ApplicationDataContainer _settings;

    public WindowsLocalSettingsMigrationStore(ApplicationDataContainer? settings = null)
    {
        _settings = settings ?? ApplicationData.Current.LocalSettings;
    }

    public ValueTask<IReadOnlyDictionary<string, object>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, object> snapshot = _settings.Values
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return ValueTask.FromResult(snapshot);
    }

    public ValueTask ApplyAsync(
        IReadOnlyDictionary<string, object> settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var previous = settings.Keys.ToDictionary(
            key => key,
            key => _settings.Values.TryGetValue(key, out var value)
                ? new PreviousValue(true, value)
                : new PreviousValue(false, null),
            StringComparer.Ordinal);
        try
        {
            foreach (var setting in settings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _settings.Values[setting.Key] = setting.Value;
            }
        }
        catch
        {
            foreach (var item in previous)
            {
                try
                {
                    if (item.Value.Existed)
                    {
                        _settings.Values[item.Key] = item.Value.Value!;
                    }
                    else
                    {
                        _settings.Values.Remove(item.Key);
                    }
                }
                catch
                {
                    // Preserve the original import failure. The neutral handoff is retained
                    // so the complete idempotent import can be retried on the next launch.
                }
            }

            throw;
        }

        return ValueTask.CompletedTask;
    }

    private readonly record struct PreviousValue(bool Existed, object? Value);
}
