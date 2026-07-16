using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Starts the packaged source helper through the Windows elevation prompt, authenticates a
/// private one-shot pipe, sends one allowlisted request, and accepts exactly one result.
/// </summary>
public sealed class ElevatedSourceManagementBroker : IPrivilegedSourceManagementBroker
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(10);

    private readonly string _helperPath;
    private readonly TimeSpan _connectionTimeout;

    public ElevatedSourceManagementBroker(
        string? helperPath = null,
        TimeSpan? connectionTimeout = null)
    {
        _helperPath = helperPath
            ?? Path.Combine(AppContext.BaseDirectory, "PackagePilot.SourceAdmin.exe");
        _connectionTimeout = connectionTimeout ?? DefaultConnectionTimeout;
    }

    public async Task<SourceOperationResult> ExecuteElevatedAsync(
        PrivilegedSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = PrivilegedSourceRequestValidator.Validate(request);
        if (!validation.IsValid)
        {
            return Failure(
                request,
                SourceOperationStatus.InvalidRequest,
                string.Join(" ", validation.Errors));
        }

        if (!File.Exists(_helperPath))
        {
            return Failure(
                request,
                SourceOperationStatus.Unavailable,
                "The packaged source-administration helper is unavailable.");
        }

        var pipeName = SourceAdminPipeProtocol.CreatePipeName();
        var secret = SourceAdminPipeProtocol.CreateSecret();
        using var pipe = ElevatedPipeAclFactory.CreateServerForCurrentUser(pipeName);

        Process? process = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process = StartElevatedHelper(pipeName, secret);

            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            connectionCts.CancelAfter(_connectionTimeout);
            await WaitForConnectionOrHelperExitAsync(
                pipe,
                process,
                connectionCts.Token).ConfigureAwait(false);
            VerifyClientProcess(pipe.SafePipeHandle, process.Id);
            await SourceAdminPipeProtocol.AuthenticateServerAsync(
                pipe,
                pipeName,
                secret,
                connectionCts.Token).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await SourceAdminPipeProtocol.WriteJsonAsync(
                pipe,
                request,
                connectionCts.Token).ConfigureAwait(false);

            // Once a mutation has crossed the privilege boundary, do not abandon the helper
            // because the foreground view was closed. Keep a finite hard timeout and collect its
            // one result so the caller can refresh source state accurately.
            using var operationCts = new CancellationTokenSource(OperationTimeout);
            var response = await SourceAdminPipeProtocol.ReadJsonAsync<PrivilegedSourceResponse>(
                pipe,
                operationCts.Token).ConfigureAwait(false);

            if (response.SchemaVersion != PrivilegedSourceRequest.CurrentSchemaVersion
                || response.RequestId != request.RequestId
                || response.Result is null
                || response.Result.OperationId == Guid.Empty
                || response.Result.Kind != request.Kind)
            {
                return Failure(
                    request,
                    SourceOperationStatus.Failed,
                    "The elevated source helper returned an invalid response.");
            }

            return response.Result;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return Failure(
                request,
                SourceOperationStatus.AccessDenied,
                "Administrator approval was canceled.",
                exception.HResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return Failure(
                request,
                SourceOperationStatus.Failed,
                "The elevated source helper did not respond in time.",
                exception.HResult);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(
                request,
                SourceOperationStatus.AccessDenied,
                "The helper must run under the same Windows account. Alternate administrator " +
                    "credentials are rejected to avoid mutating another user's source profile.",
                exception.HResult);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            return Failure(
                request,
                SourceOperationStatus.Failed,
                "The elevated source helper could not complete the request.",
                exception.HResult);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private Process StartElevatedHelper(string pipeName, string secret)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--secret");
        startInfo.ArgumentList.Add(secret);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows did not start the source-administration helper.");
    }

    private static void VerifyClientProcess(SafePipeHandle pipeHandle, int expectedProcessId)
    {
        if (!GetNamedPipeClientProcessId(pipeHandle, out var clientProcessId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (clientProcessId != unchecked((uint)expectedProcessId))
        {
            throw new UnauthorizedAccessException(
                "An unexpected process connected to the source helper pipe.");
        }
    }

    private static async Task WaitForConnectionOrHelperExitAsync(
        System.IO.Pipes.NamedPipeServerStream pipe,
        Process helper,
        CancellationToken cancellationToken)
    {
        var connectionTask = pipe.WaitForConnectionAsync(cancellationToken);
        var exitTask = helper.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(connectionTask, exitTask).ConfigureAwait(false);
        if (completed == exitTask && !pipe.IsConnected)
        {
            await exitTask.ConfigureAwait(false);
            throw new UnauthorizedAccessException(
                "The elevated helper exited before authenticating the source pipe.");
        }

        await connectionTask.ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    private static SourceOperationResult Failure(
        PrivilegedSourceRequest request,
        SourceOperationStatus status,
        string message,
        int? hresult = null) =>
        new()
        {
            OperationId = request.RequestId,
            Kind = request.Kind,
            SourceName = request.Kind == SourceOperationKind.Add
                ? request.AddRequest?.Name?.Trim() ?? string.Empty
                : request.SourceName?.Trim() ?? string.Empty,
            Status = status,
            Message = message,
            HResult = hresult
        };
}
