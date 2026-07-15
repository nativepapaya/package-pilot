using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

public interface IAppBehaviorSettingsStore
{
    ValueTask<AppBehaviorSettings> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        AppBehaviorSettings settings,
        CancellationToken cancellationToken = default);
}
