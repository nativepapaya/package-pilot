using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

public sealed class InstalledAppInventory : IInstalledAppInventory
{
    private readonly IInstalledAppProvider[] _providers;
    private readonly IInstalledAppMerger _merger;
    private readonly TimeProvider _timeProvider;

    public InstalledAppInventory(
        IEnumerable<IInstalledAppProvider> providers,
        IInstalledAppMerger? merger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers.ToArray();
        _merger = merger ?? new ExactInstalledAppMerger();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InstalledAppSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var reads = await Task.WhenAll(_providers.Select(provider =>
            ReadProviderAsync(provider, cancellationToken)));
        cancellationToken.ThrowIfCancellationRequested();

        return new InstalledAppSnapshot
        {
            CapturedAt = _timeProvider.GetUtcNow(),
            Apps = _merger.Merge(reads.SelectMany(read => read.Result.Installations)),
            Providers = reads
                .Select(read => new InstalledAppProviderStatus
                {
                    ProviderId = read.Provider.Id,
                    Provider = read.Provider.Kind,
                    Health = read.Result.Health,
                    InstallationCount = read.Result.Installations.Count,
                    Message = read.Result.Message
                })
                .OrderBy(status => status.Provider)
                .ThenBy(status => status.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private static async Task<ProviderRead> ReadProviderAsync(
        IInstalledAppProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await provider.ReadAsync(cancellationToken);
            return new ProviderRead(provider, result ?? new InstalledAppProviderResult
            {
                Health = InventoryProviderHealth.Unavailable,
                Message = "The inventory provider returned no result."
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return new ProviderRead(provider, new InstalledAppProviderResult
            {
                Health = InventoryProviderHealth.Unavailable,
                Message = exception.Message
            });
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record ProviderRead(
        IInstalledAppProvider Provider,
        InstalledAppProviderResult Result);
}
