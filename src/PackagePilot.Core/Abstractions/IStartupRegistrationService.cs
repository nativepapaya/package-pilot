using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

public interface IStartupRegistrationService
{
    Task<StartupRegistrationResult> GetStateAsync(
        CancellationToken cancellationToken = default);

    Task<StartupRegistrationResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);
}
