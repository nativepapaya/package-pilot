using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.App.ViewModels;

internal abstract class DestinationPageViewModel<TState>(TState initialState)
    where TState : class
{
    public TState State { get; private set; } = initialState;
    public long Revision { get; private set; }
    public DestinationChangeFlags LastChanges { get; private set; }

    public void Apply(TState state, DestinationChangeFlags changes)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        LastChanges = changes;
        Revision++;
    }
}

internal sealed record DiscoverPageState(
    IReadOnlyList<PackageSummary> Results,
    bool IsSearching,
    bool MutationActionsAvailable);

internal sealed class DiscoverPageViewModel()
    : DestinationPageViewModel<DiscoverPageState>(new([], false, false));

internal sealed record InstalledPageState(
    IReadOnlyList<InstalledApp> Apps,
    IReadOnlyList<PackageSummary> WingetPackages,
    IReadOnlyList<InstalledAppProviderStatus> Providers,
    bool IsAuthoritative,
    bool MutationActionsAvailable);

internal sealed class InstalledPageViewModel()
    : DestinationPageViewModel<InstalledPageState>(new([], [], [], false, false));

internal sealed record UpdatesPageState(
    IReadOnlyList<PackageSummary> Updates,
    IReadOnlyList<PackageSummary> PendingVerification,
    UpdateCheckState CheckState,
    DateTimeOffset? LastAttempt,
    DateTimeOffset? LastSuccess,
    string? Error);

internal sealed class UpdatesPageViewModel()
    : DestinationPageViewModel<UpdatesPageState>(
        new([], [], UpdateCheckState.NotChecked, null, null, null));

internal sealed record ActivityDestinationState(
    OperationQueueSnapshot Queue,
    IReadOnlyList<MutationVerificationMarker> PendingVerification);

internal sealed class ActivityPageViewModel()
    : DestinationPageViewModel<ActivityDestinationState>(new(new(), []));

internal sealed record SourcesPageState(
    IReadOnlyList<PackageSourceInfo> Sources,
    SourceManagementCapabilities Capabilities,
    bool IsManaging);

internal sealed class SourcesPageViewModel()
    : DestinationPageViewModel<SourcesPageState>(new([], new(), false));

internal sealed record SettingsPageState(
    WindowsIntegrationCapabilities WindowsCapabilities,
    IReadOnlyList<PackageSourceStatus> SourceHealth,
    string CapabilitySummary);

internal sealed class SettingsPageViewModel()
    : DestinationPageViewModel<SettingsPageState>(new(new(), [], string.Empty));
