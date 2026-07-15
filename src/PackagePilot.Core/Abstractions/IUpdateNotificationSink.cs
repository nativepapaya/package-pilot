using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>Applies a platform notification and badge decision.</summary>
public interface IUpdateNotificationSink
{
    Task ApplyAsync(
        UpdateNotificationDecision decision,
        CancellationToken cancellationToken = default);
}

public interface IBackgroundUpdateRunStatusStore
{
    Task<BackgroundUpdateRunStatus?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        BackgroundUpdateRunStatus status,
        CancellationToken cancellationToken = default);
}
