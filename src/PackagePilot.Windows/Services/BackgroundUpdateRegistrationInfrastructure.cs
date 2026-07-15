using System.Text.Json;
using System.Text.Json.Serialization;
using PackagePilot.Core.Models;
using Windows.ApplicationModel.Background;
using Windows.Storage;

namespace PackagePilot.Windows.Services;

internal sealed record BackgroundTaskRegistrationDescriptor(Guid Id, string Name);

internal interface IBackgroundTaskRegistrationPlatform
{
    Task<BackgroundAccessStatus> RequestAccessAsync();

    IReadOnlyList<BackgroundTaskRegistrationDescriptor> GetRegistrations();

    BackgroundTaskRegistrationDescriptor Register(string name, uint intervalMinutes);

    void Unregister(Guid registrationId);
}

internal sealed class WindowsBackgroundTaskRegistrationPlatform : IBackgroundTaskRegistrationPlatform
{
    public async Task<BackgroundAccessStatus> RequestAccessAsync() =>
        await BackgroundExecutionManager.RequestAccessAsync();

    public IReadOnlyList<BackgroundTaskRegistrationDescriptor> GetRegistrations() =>
        BackgroundTaskRegistration.AllTasks
            .Select(pair => new BackgroundTaskRegistrationDescriptor(pair.Key, pair.Value.Name))
            .ToArray();

    public BackgroundTaskRegistrationDescriptor Register(string name, uint intervalMinutes)
    {
        var builder = new BackgroundTaskBuilder
        {
            Name = name,
            CancelOnConditionLoss = true
        };
        builder.SetTaskEntryPointClsid(WindowsIntegrationConstants.BackgroundTaskClassId);
        builder.SetTrigger(new TimeTrigger(intervalMinutes, oneShot: false));
        builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
        var registration = builder.Register();
        return new BackgroundTaskRegistrationDescriptor(
            registration.TaskId,
            registration.Name);
    }

    public void Unregister(Guid registrationId)
    {
        var registration = BackgroundTaskRegistration.AllTasks
            .FirstOrDefault(pair => pair.Key == registrationId)
            .Value;
        registration?.Unregister(cancelTask: true);
    }
}

internal sealed record BackgroundRegistrationMetadata
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public UpdateMonitoringCadence DesiredCadence { get; init; } = UpdateMonitoringCadence.Daily;
    public UpdateMonitoringCadence ConfirmedCadence { get; init; } = UpdateMonitoringCadence.Manual;
    public Guid RegistrationId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public DateTimeOffset? RegisteredAt { get; init; }
    public DateTimeOffset LastAttemptAt { get; init; }
    public string? LastError { get; init; }

    [JsonIgnore]
    public bool HasConfirmedRegistration =>
        ConfirmedCadence != UpdateMonitoringCadence.Manual
        && RegistrationId != Guid.Empty
        && !string.IsNullOrWhiteSpace(TaskName);
}

internal interface IBackgroundRegistrationMetadataStore
{
    BackgroundRegistrationMetadata? Load();

    void Save(BackgroundRegistrationMetadata metadata);

    void Clear();
}

internal sealed class ApplicationDataBackgroundRegistrationMetadataStore
    : IBackgroundRegistrationMetadataStore
{
    private const string SettingsKey = "backgroundUpdateRegistrationMetadata";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly ApplicationDataContainer _settings;

    public ApplicationDataBackgroundRegistrationMetadataStore()
        : this(ApplicationData.Current.LocalSettings)
    {
    }

    internal ApplicationDataBackgroundRegistrationMetadataStore(
        ApplicationDataContainer settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public BackgroundRegistrationMetadata? Load()
    {
        if (!_settings.Values.TryGetValue(SettingsKey, out var stored)
            || stored is not string json
            || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<BackgroundRegistrationMetadata>(
                json,
                SerializerOptions);
            if (metadata is null
                || metadata.SchemaVersion != BackgroundRegistrationMetadata.CurrentSchemaVersion
                || !Enum.IsDefined(metadata.DesiredCadence)
                || !Enum.IsDefined(metadata.ConfirmedCadence)
                || (metadata.ConfirmedCadence != UpdateMonitoringCadence.Manual
                    && !metadata.HasConfirmedRegistration))
            {
                return null;
            }

            return metadata;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(BackgroundRegistrationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _settings.Values[SettingsKey] = JsonSerializer.Serialize(metadata, SerializerOptions);
    }

    public void Clear() => _settings.Values.Remove(SettingsKey);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}
