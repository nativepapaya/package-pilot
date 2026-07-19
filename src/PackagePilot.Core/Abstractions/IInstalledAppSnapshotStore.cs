using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

public interface IInstalledAppSnapshotStore
{
    Task<InstalledAppSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(InstalledAppSnapshot snapshot, CancellationToken cancellationToken = default);
}
