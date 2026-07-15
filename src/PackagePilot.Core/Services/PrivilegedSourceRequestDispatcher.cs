using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Maps the allowlisted wire contract to the strongly typed source-management API.
/// No shell command or registry uninstall command can be represented here.
/// </summary>
public static class PrivilegedSourceRequestDispatcher
{
    public static Task<SourceOperationResult> DispatchAsync(
        ISourceManagementService service,
        PrivilegedSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);

        var validation = PrivilegedSourceRequestValidator.Validate(request);
        if (!validation.IsValid)
        {
            return Task.FromResult(new SourceOperationResult
            {
                OperationId = request.RequestId,
                Kind = request.Kind,
                SourceName = GetSourceName(request),
                Status = SourceOperationStatus.InvalidRequest,
                Message = string.Join(" ", validation.Errors)
            });
        }

        return request.Kind switch
        {
            SourceOperationKind.Add => service.AddSourceAsync(
                request.AddRequest!,
                cancellationToken: cancellationToken),
            SourceOperationKind.Remove => service.RemoveSourceAsync(
                request.SourceName,
                cancellationToken: cancellationToken),
            SourceOperationKind.Reset => service.ResetSourceAsync(
                new ResetPackageSourceRequest
                {
                    SourceName = request.SourceName,
                    IsConfirmed = request.IsResetConfirmed
                },
                cancellationToken: cancellationToken),
            SourceOperationKind.EditExplicit => service.SetSourceExplicitAsync(
                request.SourceName,
                request.IsExplicit!.Value,
                cancellationToken: cancellationToken),
            _ => Task.FromResult(new SourceOperationResult
            {
                OperationId = request.RequestId,
                Kind = request.Kind,
                SourceName = GetSourceName(request),
                Status = SourceOperationStatus.InvalidRequest,
                Message = "The requested source operation is not allowlisted for elevation."
            })
        };
    }

    private static string GetSourceName(PrivilegedSourceRequest request) =>
        request.Kind == SourceOperationKind.Add
            ? request.AddRequest?.Name?.Trim() ?? string.Empty
            : request.SourceName?.Trim() ?? string.Empty;
}
