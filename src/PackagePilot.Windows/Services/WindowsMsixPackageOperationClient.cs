using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace PackagePilot.Windows.Services;

/// <summary>Safely removes eligible current-user MSIX packages in the foreground.</summary>
public sealed class WindowsMsixPackageOperationClient : IMsixPackageOperationClient
{
    private readonly PackageManager _packageManager = new();

    public async Task<OperationResult> UninstallAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.EffectiveTarget is not MsixTarget target || string.IsNullOrWhiteSpace(target.PackageFullName))
        {
            return Failed(operation, "InvalidTarget", "The MSIX package identity is missing.");
        }

        // Cancellation is intentionally observed only before deployment begins.
        cancellationToken.ThrowIfCancellationRequested();
        Package? package;
        try
        {
            package = _packageManager.FindPackageForUser(string.Empty, target.PackageFullName);
        }
        catch (Exception ex)
        {
            return Failed(operation, $"0x{ex.HResult:X8}", ex.Message, ex.HResult);
        }

        if (package is null)
        {
            return Failed(operation, "PackageNotFound", "Windows could not find this package.");
        }

        var safetyError = GetSafetyError(package);
        if (safetyError is not null)
        {
            return Failed(operation, "ProtectedPackage", safetyError);
        }

        var startedAt = DateTimeOffset.UtcNow;
        progress?.Report(new OperationProgress
        {
            OperationId = operation.Id,
            State = PackageOperationState.Uninstalling,
            Message = "Asking Windows to remove the app…",
            CancellationSupported = false
        });

        try
        {
            var result = await _packageManager.RemovePackageAsync(
                target.PackageFullName,
                RemovalOptions.None);
            var diagnostic = CreateDeploymentDiagnostic(result.ActivityId);
            var extended = result.ExtendedErrorCode;
            if (extended is not null && extended.HResult < 0)
            {
                return Failed(
                    operation,
                    $"0x{extended.HResult:X8}",
                    string.IsNullOrWhiteSpace(result.ErrorText) ? extended.Message : result.ErrorText,
                    extended.HResult,
                    startedAt,
                    diagnostic);
            }

            return new OperationResult
            {
                OperationId = operation.Id,
                Package = operation.Package,
                Target = target,
                Kind = PackageOperationKind.Uninstall,
                State = PackageOperationState.Completed,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Diagnostic = diagnostic
            };
        }
        catch (Exception ex)
        {
            return Failed(operation, $"0x{ex.HResult:X8}", ex.Message, ex.HResult, startedAt);
        }
    }

    private static string? GetSafetyError(Package package)
    {
        if (string.Equals(
                package.Id.FamilyName,
                Package.Current.Id.FamilyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Package Pilot cannot remove itself.";
        }

        if (package.SignatureKind == PackageSignatureKind.System)
        {
            return "Windows system packages cannot be removed by Package Pilot.";
        }

        if (package.IsFramework || package.IsResourcePackage || package.IsOptional)
        {
            return "Framework, resource, and optional packages are protected from direct removal.";
        }

        return null;
    }

    private static OperationResult Failed(
        PackageOperation operation,
        string code,
        string message,
        int? hResult = null,
        DateTimeOffset? startedAt = null,
        OperationDiagnosticReference? diagnostic = null)
    {
        var kind = code is "ProtectedPackage"
            ? WingetErrorKind.PolicyBlocked
            : WindowsPackageOperationErrors.ClassifyAdministratorRequirement(
                hResult,
                WingetErrorKind.ComFailure);
        var effectiveMessage = kind == WingetErrorKind.AdministratorRequired
            ? WindowsPackageOperationErrors.GetAdministratorRequiredMessage(operation.Kind)
            : string.IsNullOrWhiteSpace(message)
                ? "Windows could not remove this app."
                : message;

        return new OperationResult
        {
            OperationId = operation.Id,
            Package = operation.Package,
            Target = operation.EffectiveTarget,
            Kind = operation.Kind,
            State = PackageOperationState.Failed,
            StartedAt = startedAt ?? DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Error = new WingetError
            {
                Kind = kind,
                Code = code,
                Message = effectiveMessage,
                HResult = hResult
            },
            Diagnostic = diagnostic
        };
    }

    private static OperationDiagnosticReference? CreateDeploymentDiagnostic(Guid activityId) =>
        activityId == Guid.Empty
            ? null
            : new OperationDiagnosticReference
            {
                Provider = OperationDiagnosticProvider.WindowsDeployment,
                ReferenceId = activityId
            };
}
