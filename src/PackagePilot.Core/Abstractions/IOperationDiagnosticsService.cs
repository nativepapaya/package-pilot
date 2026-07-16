using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Lazily resolves allowlisted, provider-specific diagnostic references. Implementations must
/// return plain text and must not execute package-manager or shell commands.
/// </summary>
public interface IOperationDiagnosticsService
{
    Task<OperationDiagnosticDocument> ReadAsync(
        OperationResult operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the currently available diagnostics for a queued or running operation. The read is
    /// a point-in-time snapshot; callers control any bounded foreground refresh cadence.
    /// </summary>
    Task<OperationDiagnosticDocument> ReadLiveAsync(
        OperationQueueEntry operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes only app-owned installer logs for the supplied references. Provider-owned
    /// WinGet and Windows event logs are never deleted.
    /// </summary>
    Task DeleteOwnedLogsAsync(
        IReadOnlyCollection<OperationDiagnosticReference> diagnostics,
        CancellationToken cancellationToken = default);
}
