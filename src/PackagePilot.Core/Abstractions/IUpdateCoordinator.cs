using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

public interface IUpdateSnapshotStore
{
    Task<UpdateSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UpdateSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>Coordinates freshness policy, persistence, and in-process update-check coalescing.</summary>
public interface IUpdateCoordinator
{
    Task<UpdateSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    UpdateCheckState GetState(UpdateSnapshot? snapshot);

    bool ShouldAutomaticallyCheck(UpdateSnapshot? snapshot);

    Task<UpdateCheckResult> CheckAsync(
        UpdateCheckReason reason,
        IReadOnlyList<PackageSourceStatus>? sourceStatuses = null,
        CancellationToken cancellationToken = default);
}
