namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Provides the package-identity-scoped application settings used by the one-time
/// development-to-production identity migration.
/// </summary>
public interface IIdentityMigrationSettingsStore
{
    ValueTask<IReadOnlyDictionary<string, object>> ReadAllAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the supplied settings idempotently. Implementations should roll back
    /// partial changes when their backing store supports it.
    /// </summary>
    ValueTask ApplyAsync(
        IReadOnlyDictionary<string, object> settings,
        CancellationToken cancellationToken = default);
}
