using System.Runtime.InteropServices;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using Windows.ApplicationModel.Background;
using Windows.Storage;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Registers an opportunistic read-only update trigger and reconciles its actual cadence with
/// durable registration metadata. All foreground instances share one transaction gate so launch
/// reconciliation and Settings cannot create competing registrations.
/// </summary>
public sealed class WindowsBackgroundUpdateRegistrationService
    : IBackgroundUpdateRegistrationService
{
    internal const uint DailyMinutes = 1_440;
    internal const uint SixHourMinutes = 360;
    private const string DailyTaskSuffix = ".v2.daily";
    private const string SixHourTaskSuffix = ".v2.sixHours";
    private static readonly SemaphoreSlim ConfigurationGate = new(1, 1);

    private readonly IBackgroundTaskRegistrationPlatform _platform;
    private readonly IBackgroundRegistrationMetadataStore _metadataStore;
    private readonly Func<UpdateMonitoringCadence> _desiredCadence;
    private readonly TimeProvider _timeProvider;

    public WindowsBackgroundUpdateRegistrationService()
        : this(
            new WindowsBackgroundTaskRegistrationPlatform(),
            new ApplicationDataBackgroundRegistrationMetadataStore(),
            ReadDesiredCadence,
            TimeProvider.System)
    {
    }

    internal WindowsBackgroundUpdateRegistrationService(
        IBackgroundTaskRegistrationPlatform platform,
        IBackgroundRegistrationMetadataStore metadataStore,
        Func<UpdateMonitoringCadence> desiredCadence,
        TimeProvider? timeProvider = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
        _desiredCadence = desiredCadence ?? throw new ArgumentNullException(nameof(desiredCadence));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BackgroundMonitoringResult> ConfigureAsync(
        UpdateMonitoringCadence cadence,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(cadence))
        {
            return Failed(
                BackgroundMonitoringState.Failed,
                UpdateMonitoringCadence.Daily,
                "The requested background monitoring cadence is invalid.");
        }

        await ConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ConfigureCoreAsync(cadence, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ConfigurationGate.Release();
        }
    }

    private async Task<BackgroundMonitoringResult> ConfigureCoreAsync(
        UpdateMonitoringCadence cadence,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BackgroundTaskRegistrationDescriptor> existing =
            Array.Empty<BackgroundTaskRegistrationDescriptor>();
        BackgroundRegistrationMetadata? previousMetadata = null;
        BackgroundTaskRegistrationDescriptor? created = null;
        var registrationsChanged = false;

        try
        {
            previousMetadata = _metadataStore.Load();
            existing = GetOwnedRegistrations();
            if (cadence == UpdateMonitoringCadence.Manual)
            {
                foreach (var registration in existing)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    registrationsChanged = true;
                    _platform.Unregister(registration.Id);
                }

                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoOwnedRegistrations();
                SaveMetadata(
                    cadence,
                    UpdateMonitoringCadence.Manual,
                    registration: null,
                    lastError: null,
                    previousMetadata);
                return new BackgroundMonitoringResult
                {
                    State = BackgroundMonitoringState.Disabled,
                    Cadence = cadence,
                    Message = "Background update discovery is disabled."
                };
            }

            var matching = existing
                .Where(registration =>
                    TryGetRegisteredCadence(registration.Name, out var actual)
                    && actual == cadence)
                .ToArray();
            if (matching.Length > 0)
            {
                var primary = matching.FirstOrDefault(item => item.Id == previousMetadata?.RegistrationId)
                    ?? matching[0];
                foreach (var registration in existing.Where(item => item.Id != primary.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    registrationsChanged = true;
                    _platform.Unregister(registration.Id);
                }

                cancellationToken.ThrowIfCancellationRequested();
                EnsureOnlyRegistration(primary.Id);
                SaveMetadata(cadence, cadence, primary, lastError: null, previousMetadata);
                return Registered(cadence);
            }

            // Preserve the current registration until Windows grants access and accepts the
            // replacement. Denial or registration failure therefore leaves monitoring intact.
            var access = await _platform.RequestAccessAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAllowed(access))
            {
                const string message =
                    "Windows did not allow Package Pilot to change background update discovery. The prior registration was preserved.";
                SaveFailureMetadata(cadence, existing, previousMetadata, message);
                return new BackgroundMonitoringResult
                {
                    State = BackgroundMonitoringState.Denied,
                    Cadence = cadence,
                    Message = message
                };
            }

            created = _platform.Register(
                GetTaskName(cadence),
                GetIntervalMinutes(cadence));
            registrationsChanged = true;
            foreach (var registration in existing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _platform.Unregister(registration.Id);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureOnlyRegistration(created.Id);
            SaveMetadata(cadence, cadence, created, lastError: null, previousMetadata);
            return Registered(cadence);
        }
        catch (OperationCanceledException)
        {
            if (registrationsChanged)
            {
                TryRestorePriorRegistration(existing, previousMetadata, created);
            }

            TrySaveFailureMetadata(
                cadence,
                previousMetadata,
                "Background monitoring configuration was cancelled.");
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            if (registrationsChanged)
            {
                TryRestorePriorRegistration(existing, previousMetadata, created);
            }

            TrySaveFailureMetadata(cadence, previousMetadata, ex.Message);
            return Failed(ToFailureState(ex), cadence, ex.Message);
        }
    }

    public BackgroundMonitoringResult GetCurrent()
    {
        var desired = UpdateMonitoringCadence.Daily;
        if (!ConfigurationGate.Wait(0))
        {
            try
            {
                desired = _desiredCadence();
            }
            catch (Exception)
            {
            }

            return Failed(
                BackgroundMonitoringState.Failed,
                desired,
                "Background monitoring configuration is already in progress.");
        }

        BackgroundRegistrationMetadata? metadata = null;
        try
        {
            desired = _desiredCadence();
            metadata = _metadataStore.Load();
            var registrations = GetOwnedRegistrations();
            if (registrations.Count == 0)
            {
                var message = desired == UpdateMonitoringCadence.Manual
                    ? null
                    : "The configured background update task is not registered.";
                SaveMetadata(
                    desired,
                    UpdateMonitoringCadence.Manual,
                    registration: null,
                    message,
                    metadata);
                return new BackgroundMonitoringResult
                {
                    State = BackgroundMonitoringState.Disabled,
                    Cadence = desired,
                    Message = message ?? "Background update discovery is disabled."
                };
            }

            var selected = SelectConfirmedRegistration(registrations, metadata);
            if (desired == UpdateMonitoringCadence.Manual)
            {
                const string message =
                    "A background update task remains registered while Manual monitoring is selected.";
                SaveFailureMetadata(desired, registrations, metadata, message);
                return Failed(BackgroundMonitoringState.Failed, desired, message);
            }

            if (registrations.Count != 1)
            {
                const string message =
                    "Multiple Package Pilot background update tasks are registered and require reconciliation.";
                SaveFailureMetadata(desired, registrations, metadata, message);
                return Failed(BackgroundMonitoringState.Failed, desired, message);
            }

            var registration = registrations[0];
            if (!TryGetRegisteredCadence(registration.Name, out var actual))
            {
                const string message =
                    "The existing background task uses legacy cadence metadata and requires reconciliation.";
                SaveMetadata(
                    desired,
                    selected.Cadence,
                    selected.Registration,
                    message,
                    metadata);
                return Failed(BackgroundMonitoringState.Failed, desired, message);
            }

            if (actual != desired)
            {
                var message =
                    $"The registered background cadence is {DescribeCadence(actual)}, but {DescribeCadence(desired)} is selected.";
                SaveMetadata(desired, actual, registration, message, metadata);
                return Failed(BackgroundMonitoringState.Failed, desired, message);
            }

            SaveMetadata(desired, actual, registration, lastError: null, metadata);
            return Registered(actual);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            try
            {
                var registration = metadata?.HasConfirmedRegistration == true
                    ? new BackgroundTaskRegistrationDescriptor(
                        metadata.RegistrationId,
                        metadata.TaskName)
                    : null;
                SaveMetadata(
                    desired,
                    metadata?.ConfirmedCadence ?? UpdateMonitoringCadence.Manual,
                    registration,
                    ex.Message,
                    metadata);
            }
            catch (Exception)
            {
            }

            return Failed(ToFailureState(ex), desired, ex.Message);
        }
        finally
        {
            ConfigurationGate.Release();
        }
    }

    public BackgroundMonitoringStatus GetStatus(BackgroundUpdateRunStatus? lastRun = null)
    {
        var current = GetCurrent();
        var metadata = _metadataStore.Load();
        var desired = metadata?.DesiredCadence ?? current.Cadence;
        var actual = metadata?.ConfirmedCadence ??
            (current.State == BackgroundMonitoringState.Registered
                ? current.Cadence
                : UpdateMonitoringCadence.Manual);
        bool healthy = desired == UpdateMonitoringCadence.Manual
            ? actual == UpdateMonitoringCadence.Manual
                && current.State == BackgroundMonitoringState.Disabled
            : actual == desired
                && current.State == BackgroundMonitoringState.Registered;
        var runFailed = lastRun is
        {
            State: BackgroundUpdateRunState.Failed or BackgroundUpdateRunState.Cancelled
        };

        return new BackgroundMonitoringStatus
        {
            DesiredCadence = desired,
            ActualCadence = actual,
            RegistrationState = current.State,
            RegistrationHealthy = healthy,
            RegistrationAttemptedAt = metadata?.LastAttemptAt,
            LastAttemptAt = lastRun?.AttemptedAt,
            LastSuccessAt = lastRun?.LastSuccessfulRunAt,
            Error = metadata?.LastError
                ?? (runFailed || lastRun?.ForegroundFallbackRequired == true
                    ? lastRun?.Message
                    : null)
                ?? (healthy ? null : current.Message),
            ForegroundFallbackRequired = lastRun?.ForegroundFallbackRequired == true
        };
    }

    internal static string GetTaskName(UpdateMonitoringCadence cadence) =>
        WindowsIntegrationConstants.BackgroundTaskName + (cadence switch
        {
            UpdateMonitoringCadence.EverySixHours => SixHourTaskSuffix,
            _ => DailyTaskSuffix
        });

    internal static bool TryGetRegisteredCadence(
        string? taskName,
        out UpdateMonitoringCadence cadence)
    {
        if (string.Equals(
            taskName,
            WindowsIntegrationConstants.BackgroundTaskName + DailyTaskSuffix,
            StringComparison.Ordinal))
        {
            cadence = UpdateMonitoringCadence.Daily;
            return true;
        }

        if (string.Equals(
            taskName,
            WindowsIntegrationConstants.BackgroundTaskName + SixHourTaskSuffix,
            StringComparison.Ordinal))
        {
            cadence = UpdateMonitoringCadence.EverySixHours;
            return true;
        }

        cadence = UpdateMonitoringCadence.Manual;
        return false;
    }

    private void EnsureNoOwnedRegistrations()
    {
        if (GetOwnedRegistrations().Count != 0)
        {
            throw new InvalidOperationException(
                "Windows did not remove every Package Pilot background task.");
        }
    }

    private void EnsureOnlyRegistration(Guid registrationId)
    {
        var actual = GetOwnedRegistrations();
        if (actual.Count != 1 || actual[0].Id != registrationId)
        {
            throw new InvalidOperationException(
                "Windows did not confirm exactly one Package Pilot background task.");
        }
    }

    private void TryRestorePriorRegistration(
        IReadOnlyList<BackgroundTaskRegistrationDescriptor> previous,
        BackgroundRegistrationMetadata? previousMetadata,
        BackgroundTaskRegistrationDescriptor? created)
    {
        var previousIds = previous.Select(item => item.Id).ToHashSet();
        try
        {
            foreach (var registration in GetOwnedRegistrations()
                         .Where(item => !previousIds.Contains(item.Id)))
            {
                _platform.Unregister(registration.Id);
            }
        }
        catch (Exception)
        {
            if (created is not null)
            {
                try
                {
                    _platform.Unregister(created.Id);
                }
                catch (Exception)
                {
                }
            }
        }

        var prior = SelectConfirmedRegistration(previous, previousMetadata);
        if (prior.Registration is null || prior.Cadence == UpdateMonitoringCadence.Manual)
        {
            return;
        }

        try
        {
            var current = GetOwnedRegistrations();
            if (current.Any(item =>
                TryGetRegisteredCadence(item.Name, out var cadence)
                && cadence == prior.Cadence))
            {
                return;
            }

            _platform.Register(GetTaskName(prior.Cadence), GetIntervalMinutes(prior.Cadence));
        }
        catch (Exception)
        {
            // GetCurrent will expose the mismatch and the next launch will reconcile it.
        }
    }

    private void TrySaveFailureMetadata(
        UpdateMonitoringCadence desired,
        BackgroundRegistrationMetadata? previousMetadata,
        string message)
    {
        try
        {
            SaveFailureMetadata(desired, GetOwnedRegistrations(), previousMetadata, message);
        }
        catch (Exception)
        {
            try
            {
                var registration = previousMetadata?.HasConfirmedRegistration == true
                    ? new BackgroundTaskRegistrationDescriptor(
                        previousMetadata.RegistrationId,
                        previousMetadata.TaskName)
                    : null;
                SaveMetadata(
                    desired,
                    previousMetadata?.ConfirmedCadence ?? UpdateMonitoringCadence.Manual,
                    registration,
                    message,
                    previousMetadata);
            }
            catch (Exception)
            {
                // The task state remains authoritative when diagnostics cannot be persisted.
            }
        }
    }

    private void SaveFailureMetadata(
        UpdateMonitoringCadence desired,
        IReadOnlyList<BackgroundTaskRegistrationDescriptor> registrations,
        BackgroundRegistrationMetadata? previousMetadata,
        string message)
    {
        var selected = SelectConfirmedRegistration(registrations, previousMetadata);
        SaveMetadata(
            desired,
            selected.Cadence,
            selected.Registration,
            message,
            previousMetadata);
    }

    private void SaveMetadata(
        UpdateMonitoringCadence desired,
        UpdateMonitoringCadence confirmed,
        BackgroundTaskRegistrationDescriptor? registration,
        string? lastError,
        BackgroundRegistrationMetadata? previousMetadata)
    {
        var sameRegistration = registration is not null
            && previousMetadata?.RegistrationId == registration.Id;
        _metadataStore.Save(new BackgroundRegistrationMetadata
        {
            DesiredCadence = desired,
            ConfirmedCadence = confirmed,
            RegistrationId = registration?.Id ?? Guid.Empty,
            TaskName = registration?.Name ?? string.Empty,
            RegisteredAt = registration is null
                ? null
                : sameRegistration
                    ? previousMetadata?.RegisteredAt
                    : _timeProvider.GetUtcNow(),
            LastAttemptAt = _timeProvider.GetUtcNow(),
            LastError = NormalizeError(lastError)
        });
    }

    private static (BackgroundTaskRegistrationDescriptor? Registration, UpdateMonitoringCadence Cadence)
        SelectConfirmedRegistration(
            IReadOnlyList<BackgroundTaskRegistrationDescriptor> registrations,
            BackgroundRegistrationMetadata? metadata)
    {
        var selected = registrations.FirstOrDefault(item => item.Id == metadata?.RegistrationId)
            ?? registrations.FirstOrDefault(item => TryGetRegisteredCadence(item.Name, out _));
        if (selected is null)
        {
            return (null, UpdateMonitoringCadence.Manual);
        }

        if (TryGetRegisteredCadence(selected.Name, out var cadence))
        {
            return (selected, cadence);
        }

        var fallback = metadata is
            { ConfirmedCadence: UpdateMonitoringCadence.Daily or UpdateMonitoringCadence.EverySixHours }
                ? metadata.ConfirmedCadence
                : UpdateMonitoringCadence.Daily;
        return (selected, fallback);
    }

    private IReadOnlyList<BackgroundTaskRegistrationDescriptor> GetOwnedRegistrations() =>
        _platform.GetRegistrations()
            .Where(registration => IsOwnedTaskName(registration.Name))
            .ToArray();

    private static bool IsOwnedTaskName(string? name) =>
        string.Equals(
            name,
            WindowsIntegrationConstants.BackgroundTaskName,
            StringComparison.Ordinal)
        || TryGetRegisteredCadence(name, out _);

    private static uint GetIntervalMinutes(UpdateMonitoringCadence cadence) => cadence switch
    {
        UpdateMonitoringCadence.EverySixHours => SixHourMinutes,
        _ => DailyMinutes
    };

    private static bool IsAllowed(BackgroundAccessStatus status) => status is
        BackgroundAccessStatus.AllowedSubjectToSystemPolicy or
        BackgroundAccessStatus.AlwaysAllowed;

    private static UpdateMonitoringCadence ReadDesiredCadence()
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        var stored = values.TryGetValue("updateMonitoringCadence", out var value)
            ? value as string
            : null;
        return UpdateMonitoringPolicy.ParseCadence(stored);
    }

    private static BackgroundMonitoringState ToFailureState(Exception exception) => exception switch
    {
        UnauthorizedAccessException => BackgroundMonitoringState.Denied,
        COMException => BackgroundMonitoringState.Unavailable,
        _ => BackgroundMonitoringState.Failed
    };

    private static string DescribeCadence(UpdateMonitoringCadence cadence) => cadence switch
    {
        UpdateMonitoringCadence.EverySixHours => "Every 6 hours",
        UpdateMonitoringCadence.Manual => "Manual",
        _ => "Daily"
    };

    private static string? NormalizeError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var normalized = string.Join(' ', message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 512 ? normalized : normalized[..512];
    }

    private static BackgroundMonitoringResult Registered(UpdateMonitoringCadence cadence) => new()
    {
        State = BackgroundMonitoringState.Registered,
        Cadence = cadence,
        Message = cadence == UpdateMonitoringCadence.EverySixHours
            ? "Windows will check approximately every six hours."
            : "Windows will check approximately once per day."
    };

    private static BackgroundMonitoringResult Failed(
        BackgroundMonitoringState state,
        UpdateMonitoringCadence cadence,
        string message) => new()
        {
            State = state,
            Cadence = cadence,
            Message = string.IsNullOrWhiteSpace(message)
                ? "Background update monitoring could not be configured."
                : message
        };
}
