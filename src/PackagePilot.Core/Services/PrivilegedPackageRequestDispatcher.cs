using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Maps the validated package-admin wire contract to exactly one strongly typed WinGet call.
/// </summary>
public static class PrivilegedPackageRequestDispatcher
{
    public static async Task<OperationResult> DispatchAsync(
        IWingetClient client,
        PrivilegedPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        var validation = PrivilegedPackageRequestValidator.Validate(request);
        if (!validation.IsValid)
        {
            return Failure(request, "InvalidElevatedRequest", string.Join(" ", validation.Errors));
        }

        var operation = request.ToOperation();
        var result = request.Kind switch
        {
            PackageOperationKind.Install =>
                await client.InstallAsync(operation, cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
            PackageOperationKind.Upgrade =>
                await client.UpgradeAsync(operation, cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
            PackageOperationKind.Uninstall =>
                await client.UninstallAsync(operation, cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
            _ => Failure(
                request,
                "InvalidElevatedOperation",
                "The requested package operation is not allowlisted for elevation.")
        };

        return result with
        {
            RanAsAdministrator = true,
            AdministratorRetryRequested = true
        };
    }

    private static OperationResult Failure(
        PrivilegedPackageRequest request,
        string code,
        string message)
    {
        var package = new PackageKey(request.PackageId, request.SourceId);
        return new OperationResult
        {
            OperationId = request.RequestId,
            Package = package,
            Target = new WingetTarget { Package = package },
            Kind = request.Kind,
            State = PackageOperationState.Failed,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            RanAsAdministrator = true,
            AdministratorRetryRequested = true,
            Error = new WingetError
            {
                Kind = WingetErrorKind.Unknown,
                Code = code,
                Message = message
            }
        };
    }
}
