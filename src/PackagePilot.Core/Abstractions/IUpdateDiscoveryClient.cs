using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Read-only boundary for discovering application updates.
/// </summary>
public interface IUpdateDiscoveryClient
{
    Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(
        CancellationToken cancellationToken = default);
}
