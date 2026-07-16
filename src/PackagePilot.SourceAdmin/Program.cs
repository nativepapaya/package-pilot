using System.Security.Principal;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using PackagePilot.Windows.Services;

namespace PackagePilot.SourceAdmin;

internal static class Program
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(10);

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
            await SourceAdminPipeProtocol.AuthenticateClientAsync(
                pipe,
                pipeName,
                secret,
                connectionCts.Token).ConfigureAwait(false);

            var request = await SourceAdminPipeProtocol.ReadJsonAsync<PrivilegedSourceRequest>(
                pipe,
                connectionCts.Token).ConfigureAwait(false);

            SourceOperationResult result;
            if (!IsElevated())
            {
                result = new SourceOperationResult
                {
                    OperationId = request.RequestId,
                    Kind = request.Kind,
                    SourceName = request.Kind == SourceOperationKind.Add
                        ? request.AddRequest?.Name?.Trim() ?? string.Empty
                        : request.SourceName?.Trim() ?? string.Empty,
                    Status = SourceOperationStatus.AccessDenied,
                    Message = "The source-administration helper is not elevated."
                };
            }
            else
            {
                using var operationCts = new CancellationTokenSource(OperationTimeout);
                result = await PrivilegedSourceRequestDispatcher.DispatchAsync(
                    new WingetClient(),
                    request,
                    operationCts.Token).ConfigureAwait(false);
            }

            await SourceAdminPipeProtocol.WriteJsonAsync(
                pipe,
                new PrivilegedSourceResponse
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
            // Deliberately avoid writing request details, credentials, or source headers to
            // stdout/stderr. The broker maps a disconnected helper to a generic failure.
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

        return SourceAdminPipeProtocol.IsValidPipeName(pipeName)
            && SourceAdminPipeProtocol.TryDecodeSecret(secret, out var decoded)
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
}
