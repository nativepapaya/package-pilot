using PackagePilot.Core.Models;

namespace PackagePilot.Core.Abstractions;

/// <summary>
/// Runs one exact WinGet package mutation through the isolated administrator helper.
/// The elevation boundary accepts no shell command, executable path, or arbitrary arguments.
/// </summary>
public interface IPrivilegedPackageOperationBroker
{
    bool IsAvailable { get; }

    Task<OperationResult> ExecuteElevatedAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
