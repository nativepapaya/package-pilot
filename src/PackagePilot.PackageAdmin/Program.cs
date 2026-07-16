using System.Security.Principal;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using PackagePilot.Windows.Services;

namespace PackagePilot.PackageAdmin;

internal static class Program
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(30);

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var pipeName, out var secret))
        {
            return 2;
        }

        try
        {
            using var connectionCts = new CancellationTokenSource(ConnectionTimeout);
            using var pipe = ElevatedPipeAclFactory.CreateClient(pipeName);
            await pipe.ConnectAsync(connectionCts.Token).ConfigureAwait(false);
            ElevatedPipeServerVerifier.VerifyTrustedAppServer(pipe.SafePipeHandle);
            await PackageAdminPipeProtocol.AuthenticateClientAsync(
                pipe,
                pipeName,
                secret,
                connectionCts.Token).ConfigureAwait(false);

            var request = await PackageAdminPipeProtocol.ReadJsonAsync<PrivilegedPackageRequest>(
                pipe,
                connectionCts.Token).ConfigureAwait(false);

            OperationResult result;
            if (!IsElevated())
            {
                result = Failure(
                    request,
                    WingetErrorKind.ElevationDenied,
                    "PackageAdminNotElevated",
                    "The package-administration helper is not elevated.",
                    ranAsAdministrator: false);
            }
            else
            {
                using var operationCts = new CancellationTokenSource(OperationTimeout);
                try
                {
                    result = await PrivilegedPackageRequestDispatcher.DispatchAsync(
                        new WingetClient(),
                        request,
                        operationCts.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException
                    and not StackOverflowException
                    and not AccessViolationException)
                {
                    result = Failure(
                        request,
                        WingetErrorKind.ComFailure,
                        "PackageAdminDispatchFailed",
                        "The elevated package operation could not be completed.",
                        ranAsAdministrator: true,
                        exception.HResult);
                }
            }

            await PackageAdminPipeProtocol.WriteJsonAsync(
                pipe,
                new PrivilegedPackageResponse
                {
                    RequestId = request.RequestId,
                    Result = result
                },
                CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // Do not emit request data, authentication material, or package metadata to a
            // console or persistent log. A disconnected helper maps to a generic broker result.
            return 3;
        }
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> args,
        out string pipeName,
        out string secret)
    {
        pipeName = string.Empty;
        secret = string.Empty;
        if (args.Count != 4)
        {
            return false;
        }

        for (var index = 0; index < args.Count; index += 2)
        {
            var value = args[index + 1];
            switch (args[index])
            {
                case "--pipe" when pipeName.Length == 0:
                    pipeName = value;
                    break;
                case "--secret" when secret.Length == 0:
                    secret = value;
                    break;
                default:
                    return false;
            }
        }

        return PackageAdminPipeProtocol.IsValidPipeName(pipeName)
            && PackageAdminPipeProtocol.TryDecodeSecret(secret, out var decoded)
            && ClearAndReturnTrue(decoded);
    }

    private static bool ClearAndReturnTrue(byte[] value)
    {
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
        return true;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static OperationResult Failure(
        PrivilegedPackageRequest request,
        WingetErrorKind kind,
        string code,
        string message,
        bool ranAsAdministrator,
        int? hresult = null)
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
            RanAsAdministrator = ranAsAdministrator,
            AdministratorRetryRequested = true,
            Error = new WingetError
            {
                Kind = kind,
                Code = code,
                Message = message,
                HResult = hresult
            }
        };
    }
}
