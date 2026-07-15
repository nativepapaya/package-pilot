using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;
using Windows.ApplicationModel.Background;

namespace PackagePilot.Tests.App;

public sealed class WindowsBackgroundUpdateRegistrationServiceTests
{
    [Fact]
    public async Task DeniedCadenceChange_PreservesExistingRegistrationAndMetadata()
    {
        var existing = Registration(UpdateMonitoringCadence.Daily);
        var platform = new FakePlatform(existing)
        {
            Access = BackgroundAccessStatus.DeniedBySystemPolicy
        };
        var store = new FakeMetadataStore
        {
            Value = Metadata(existing, UpdateMonitoringCadence.Daily)
        };
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.EverySixHours);

        var result = await service.ConfigureAsync(UpdateMonitoringCadence.EverySixHours);

        Assert.Equal(BackgroundMonitoringState.Denied, result.State);
        Assert.Equal(existing, Assert.Single(platform.Registrations));
        Assert.Equal(existing.Id, store.Value?.RegistrationId);
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, store.Value?.DesiredCadence);
        Assert.Equal(UpdateMonitoringCadence.Daily, store.Value?.ConfirmedCadence);
        Assert.Contains("prior registration", store.Value?.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["request-access"], platform.Operations);
    }

    [Fact]
    public async Task RegistrationFailure_DoesNotRemoveExistingTask()
    {
        var existing = Registration(UpdateMonitoringCadence.Daily);
        var platform = new FakePlatform(existing) { ThrowOnRegister = true };
        var store = new FakeMetadataStore
        {
            Value = Metadata(existing, UpdateMonitoringCadence.Daily)
        };
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.EverySixHours);

        var result = await service.ConfigureAsync(UpdateMonitoringCadence.EverySixHours);

        Assert.Equal(BackgroundMonitoringState.Failed, result.State);
        Assert.Equal(existing, Assert.Single(platform.Registrations));
        Assert.DoesNotContain($"unregister:{existing.Id}", platform.Operations);
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, store.Value?.DesiredCadence);
        Assert.Equal(UpdateMonitoringCadence.Daily, store.Value?.ConfirmedCadence);
        Assert.Contains("registration failed", store.Value?.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CadenceChange_RegistersReplacementBeforeRemovingPriorTask()
    {
        var existing = Registration(UpdateMonitoringCadence.Daily);
        var platform = new FakePlatform(existing);
        var store = new FakeMetadataStore
        {
            Value = Metadata(existing, UpdateMonitoringCadence.Daily)
        };
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.EverySixHours);

        var result = await service.ConfigureAsync(UpdateMonitoringCadence.EverySixHours);

        Assert.Equal(BackgroundMonitoringState.Registered, result.State);
        var replacement = Assert.Single(platform.Registrations);
        Assert.True(WindowsBackgroundUpdateRegistrationService.TryGetRegisteredCadence(
            replacement.Name,
            out var cadence));
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, cadence);
        Assert.Equal(WindowsBackgroundUpdateRegistrationService.SixHourMinutes, platform.LastInterval);
        Assert.Equal(replacement.Id, store.Value?.RegistrationId);
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, store.Value?.DesiredCadence);
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, store.Value?.ConfirmedCadence);
        Assert.Null(store.Value?.LastError);

        var registerIndex = platform.Operations.FindIndex(value => value.StartsWith("register:", StringComparison.Ordinal));
        var unregisterIndex = platform.Operations.IndexOf($"unregister:{existing.Id}");
        Assert.True(registerIndex >= 0);
        Assert.True(unregisterIndex > registerIndex);
    }

    [Fact]
    public async Task MetadataFailure_RollsBackReplacementAndPreservesPriorTask()
    {
        var existing = Registration(UpdateMonitoringCadence.Daily);
        var platform = new FakePlatform(existing);
        var store = new FakeMetadataStore
        {
            Value = Metadata(existing, UpdateMonitoringCadence.Daily),
            ThrowOnSave = true
        };
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.EverySixHours);

        var result = await service.ConfigureAsync(UpdateMonitoringCadence.EverySixHours);

        Assert.Equal(BackgroundMonitoringState.Failed, result.State);
        var restored = Assert.Single(platform.Registrations);
        Assert.True(WindowsBackgroundUpdateRegistrationService.TryGetRegisteredCadence(
            restored.Name,
            out var restoredCadence));
        Assert.Equal(UpdateMonitoringCadence.Daily, restoredCadence);
        Assert.Equal(existing.Id, store.Value?.RegistrationId);
    }

    [Fact]
    public void GetCurrent_RecoversMissingMetadataFromVersionedTaskName()
    {
        var existing = Registration(UpdateMonitoringCadence.EverySixHours);
        var platform = new FakePlatform(existing);
        var store = new FakeMetadataStore();
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.EverySixHours);

        var result = service.GetCurrent();

        Assert.Equal(BackgroundMonitoringState.Registered, result.State);
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, result.Cadence);
        Assert.Equal(existing.Id, store.Value?.RegistrationId);
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, store.Value?.DesiredCadence);
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, store.Value?.ConfirmedCadence);
        Assert.Null(store.Value?.LastError);
    }

    [Fact]
    public void GetCurrent_ReportsCadenceMismatchForLaunchReconciliation()
    {
        var existing = Registration(UpdateMonitoringCadence.Daily);
        var platform = new FakePlatform(existing);
        var store = new FakeMetadataStore
        {
            Value = Metadata(existing, UpdateMonitoringCadence.Daily)
        };
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.EverySixHours);

        var result = service.GetCurrent();

        Assert.Equal(BackgroundMonitoringState.Failed, result.State);
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, result.Cadence);
        Assert.Contains("registered background cadence", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigureMatchingCadence_CleansDuplicateOrLegacyRegistrationsWithoutNewTask()
    {
        var desired = Registration(UpdateMonitoringCadence.Daily);
        var legacy = new BackgroundTaskRegistrationDescriptor(
            Guid.NewGuid(),
            WindowsIntegrationConstants.BackgroundTaskName);
        var platform = new FakePlatform(desired, legacy);
        var store = new FakeMetadataStore();
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.Daily);

        var result = await service.ConfigureAsync(UpdateMonitoringCadence.Daily);

        Assert.Equal(BackgroundMonitoringState.Registered, result.State);
        Assert.Equal(desired, Assert.Single(platform.Registrations));
        Assert.DoesNotContain(platform.Operations, value => value == "request-access");
        Assert.DoesNotContain(platform.Operations, value => value.StartsWith("register:", StringComparison.Ordinal));
        Assert.Contains($"unregister:{legacy.Id}", platform.Operations);
    }

    [Fact]
    public async Task Manual_RemovesOwnedTasksAndClearsMetadataWithoutRequestingAccess()
    {
        var existing = Registration(UpdateMonitoringCadence.Daily);
        var platform = new FakePlatform(existing);
        var store = new FakeMetadataStore
        {
            Value = Metadata(existing, UpdateMonitoringCadence.Daily)
        };
        var service = CreateService(platform, store, UpdateMonitoringCadence.Manual);

        var result = await service.ConfigureAsync(UpdateMonitoringCadence.Manual);

        Assert.Equal(BackgroundMonitoringState.Disabled, result.State);
        Assert.Empty(platform.Registrations);
        Assert.NotNull(store.Value);
        Assert.Equal(UpdateMonitoringCadence.Manual, store.Value.DesiredCadence);
        Assert.Equal(UpdateMonitoringCadence.Manual, store.Value.ConfirmedCadence);
        Assert.Equal(Guid.Empty, store.Value.RegistrationId);
        Assert.Null(store.Value.LastError);
        Assert.DoesNotContain("request-access", platform.Operations);
    }

    [Fact]
    public async Task UnregisterFailureRollsBackReplacementAndRecordsActualCadence()
    {
        var existing = Registration(UpdateMonitoringCadence.Daily);
        var platform = new FakePlatform(existing)
        {
            ThrowOnUnregisterId = existing.Id
        };
        var store = new FakeMetadataStore
        {
            Value = Metadata(existing, UpdateMonitoringCadence.Daily)
        };
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.EverySixHours);

        var result = await service.ConfigureAsync(UpdateMonitoringCadence.EverySixHours);

        Assert.Equal(BackgroundMonitoringState.Failed, result.State);
        Assert.Equal(existing, Assert.Single(platform.Registrations));
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, store.Value?.DesiredCadence);
        Assert.Equal(UpdateMonitoringCadence.Daily, store.Value?.ConfirmedCadence);
        Assert.Contains("unregister failed", store.Value?.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationAfterReplacementCreationRestoresPriorCadence()
    {
        var existing = Registration(UpdateMonitoringCadence.Daily);
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform(existing)
        {
            OnUnregister = id =>
            {
                if (id == existing.Id)
                {
                    cancellation.Cancel();
                }
            }
        };
        var store = new FakeMetadataStore
        {
            Value = Metadata(existing, UpdateMonitoringCadence.Daily)
        };
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.EverySixHours);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConfigureAsync(
            UpdateMonitoringCadence.EverySixHours,
            cancellation.Token));

        var restored = Assert.Single(platform.Registrations);
        Assert.True(WindowsBackgroundUpdateRegistrationService.TryGetRegisteredCadence(
            restored.Name,
            out var restoredCadence));
        Assert.Equal(UpdateMonitoringCadence.Daily, restoredCadence);
        Assert.Equal(UpdateMonitoringCadence.EverySixHours, store.Value?.DesiredCadence);
        Assert.Equal(UpdateMonitoringCadence.Daily, store.Value?.ConfirmedCadence);
        Assert.Contains("cancelled", store.Value?.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentConfigurationAcrossServiceInstancesCreatesOneTask()
    {
        var accessEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakePlatform
        {
            RequestAccess = async () =>
            {
                accessEntered.TrySetResult();
                await releaseAccess.Task;
                return BackgroundAccessStatus.AllowedSubjectToSystemPolicy;
            }
        };
        var store = new FakeMetadataStore();
        var firstService = CreateService(platform, store, UpdateMonitoringCadence.Daily);
        var secondService = CreateService(platform, store, UpdateMonitoringCadence.Daily);

        var first = firstService.ConfigureAsync(UpdateMonitoringCadence.Daily);
        Task<BackgroundMonitoringResult>? second = null;
        try
        {
            await accessEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = secondService.ConfigureAsync(UpdateMonitoringCadence.Daily);

            await Task.Delay(50);
            Assert.Equal(1, platform.RequestAccessCount);
        }
        finally
        {
            releaseAccess.TrySetResult();
        }

        Assert.NotNull(second);
        var results = await Task.WhenAll(first, second!);

        Assert.All(results, result => Assert.Equal(BackgroundMonitoringState.Registered, result.State));
        Assert.Single(platform.Registrations);
        Assert.Equal(1, platform.RequestAccessCount);
        Assert.Single(
            platform.Operations,
            operation => operation.StartsWith("register:", StringComparison.Ordinal));
    }

    [Fact]
    public void CombinedStatusKeepsRequestedAndActualCadenceDistinctAfterRollback()
    {
        var existing = Registration(UpdateMonitoringCadence.Daily);
        var platform = new FakePlatform(existing);
        var store = new FakeMetadataStore
        {
            Value = Metadata(existing, UpdateMonitoringCadence.Daily) with
            {
                DesiredCadence = UpdateMonitoringCadence.EverySixHours,
                LastError = "Windows denied the cadence change."
            }
        };
        var service = CreateService(
            platform,
            store,
            UpdateMonitoringCadence.EverySixHours);
        var run = new BackgroundUpdateRunStatus
        {
            State = BackgroundUpdateRunState.Failed,
            AttemptedAt = DateTimeOffset.Parse("2026-07-15T13:00:00Z"),
            LastSuccessfulRunAt = DateTimeOffset.Parse("2026-07-15T12:00:00Z"),
            ForegroundFallbackRequired = true,
            Message = "WinGet activation failed."
        };

        var status = service.GetStatus(run);

        Assert.Equal(UpdateMonitoringCadence.EverySixHours, status.DesiredCadence);
        Assert.Equal(UpdateMonitoringCadence.Daily, status.ActualCadence);
        Assert.False(status.RegistrationHealthy);
        Assert.Equal(run.AttemptedAt, status.LastAttemptAt);
        Assert.Equal(run.LastSuccessfulRunAt, status.LastSuccessAt);
        Assert.True(status.ForegroundFallbackRequired);
        Assert.Contains("registered background cadence", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static WindowsBackgroundUpdateRegistrationService CreateService(
        FakePlatform platform,
        FakeMetadataStore store,
        UpdateMonitoringCadence desired) =>
        new(platform, store, () => desired);

    private static BackgroundTaskRegistrationDescriptor Registration(
        UpdateMonitoringCadence cadence) =>
        new(Guid.NewGuid(), WindowsBackgroundUpdateRegistrationService.GetTaskName(cadence));

    private static BackgroundRegistrationMetadata Metadata(
        BackgroundTaskRegistrationDescriptor registration,
        UpdateMonitoringCadence cadence) => new()
        {
            DesiredCadence = cadence,
            ConfirmedCadence = cadence,
            RegistrationId = registration.Id,
            TaskName = registration.Name,
            RegisteredAt = DateTimeOffset.Parse("2026-07-15T12:00:00Z"),
            LastAttemptAt = DateTimeOffset.Parse("2026-07-15T12:00:00Z")
        };

    private sealed class FakePlatform(
        params BackgroundTaskRegistrationDescriptor[] registrations)
        : IBackgroundTaskRegistrationPlatform
    {
        public List<BackgroundTaskRegistrationDescriptor> Registrations { get; } = [.. registrations];
        public List<string> Operations { get; } = [];
        public BackgroundAccessStatus Access { get; set; } =
            BackgroundAccessStatus.AllowedSubjectToSystemPolicy;
        public bool ThrowOnRegister { get; set; }
        public Guid? ThrowOnUnregisterId { get; set; }
        public Action<Guid>? OnUnregister { get; set; }
        public Func<Task<BackgroundAccessStatus>>? RequestAccess { get; set; }
        public int RequestAccessCount { get; private set; }
        public uint? LastInterval { get; private set; }

        public async Task<BackgroundAccessStatus> RequestAccessAsync()
        {
            Operations.Add("request-access");
            RequestAccessCount++;
            return RequestAccess is null ? Access : await RequestAccess();
        }

        public IReadOnlyList<BackgroundTaskRegistrationDescriptor> GetRegistrations() =>
            Registrations.ToArray();

        public BackgroundTaskRegistrationDescriptor Register(string name, uint intervalMinutes)
        {
            Operations.Add($"register:{name}");
            if (ThrowOnRegister)
            {
                throw new InvalidOperationException("registration failed");
            }

            LastInterval = intervalMinutes;
            var registration = new BackgroundTaskRegistrationDescriptor(Guid.NewGuid(), name);
            Registrations.Add(registration);
            return registration;
        }

        public void Unregister(Guid registrationId)
        {
            Operations.Add($"unregister:{registrationId}");
            OnUnregister?.Invoke(registrationId);
            if (ThrowOnUnregisterId == registrationId)
            {
                throw new InvalidOperationException("unregister failed");
            }

            Registrations.RemoveAll(item => item.Id == registrationId);
        }
    }

    private sealed class FakeMetadataStore : IBackgroundRegistrationMetadataStore
    {
        public BackgroundRegistrationMetadata? Value { get; set; }
        public bool ThrowOnSave { get; set; }

        public BackgroundRegistrationMetadata? Load() => Value;

        public void Save(BackgroundRegistrationMetadata metadata)
        {
            if (ThrowOnSave)
            {
                throw new IOException("metadata unavailable");
            }

            Value = metadata;
        }

        public void Clear() => Value = null;
    }
}
