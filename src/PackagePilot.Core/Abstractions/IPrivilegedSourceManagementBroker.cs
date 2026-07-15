using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Runs the narrow set of WinGet source mutations that require administrator approval.
/// Implementations must validate the request before crossing the elevation boundary.
/// </summary>
public interface IPrivilegedSourceManagementBroker
{
    Task<SourceOperationResult> ExecuteElevatedAsync(
        PrivilegedSourceRequest request,
        CancellationToken cancellationToken = default);
}
