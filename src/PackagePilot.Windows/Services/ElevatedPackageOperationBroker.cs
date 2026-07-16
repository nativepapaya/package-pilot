using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Launches the isolated package helper through UAC, authenticates its exact process over a
/// one-shot SID-restricted pipe, and sends one validated WinGet package mutation.
/// </summary>
public sealed class ElevatedPackageOperationBroker : IPrivilegedPackageOperationBroker
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(30);
    internal const string OutcomeUnknownCode = "ElevatedOutcomeUnknown";
    internal const string OutcomeUnknownMessage =
        "The elevated package request was sent, but Package Pilot could not confirm the outcome. " +
        "The package state must be verified before retrying.";

    private readonly string _helperPath;
    private readonly TimeSpan _connectionTimeout;
    private readonly TimeSpan _operationTimeout;
    private readonly bool _hostEligible;

    public ElevatedPackageOperationBroker(
        string? helperPath = null,
        TimeSpan? connectionTimeout = null,
        TimeSpan? operationTimeout = null)
        : this(
            helperPath,
            connectionTimeout,
            operationTimeout,
            ElevatedPipeServerVerifier.IsCurrentHostEligibleForPackageAdmin)
    {
    }

    internal ElevatedPackageOperationBroker(
        string? helperPath,
        TimeSpan? connectionTimeout,
        TimeSpan? operationTimeout,
        Func<string, bool> hostEligibility)
    {
        ArgumentNullException.ThrowIfNull(hostEligibility);
        _helperPath = helperPath
            ?? Path.Combine(AppContext.BaseDirectory, "PackagePilot.PackageAdmin.exe");
        _connectionTimeout = connectionTimeout ?? DefaultConnectionTimeout;
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        try
        {
            _hostEligible = hostEligibility(_helperPath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            _hostEligible = false;
        }
    }

    public bool IsAvailable => _hostEligible && File.Exists(_helperPath);

    public async Task<OperationResult> ExecuteElevatedAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!TryCreateRequest(operation, out var request, out var validationMessage))
        {
            return Failure(
                operation,
                WingetErrorKind.Unknown,
                "InvalidElevatedRequest",
                validationMessage,
                ranAsAdministrator: false);
        }

        if (!IsAvailable)
        {
            return Failure(
                operation,
                WingetErrorKind.ComFailure,
                "PackageAdminUnavailable",
                "The package-administration helper is unavailable.",
                ranAsAdministrator: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Report(
            progress,
            operation.Id,
            PackageOperationState.Resolving,
            "Waiting for administrator approval...",
            cancellationSupported: false);

        var pipeName = PackageAdminPipeProtocol.CreatePipeName();
        var secret = PackageAdminPipeProtocol.CreateSecret();
        using var pipe = ElevatedPipeAclFactory.CreateServerForCurrentUser(pipeName);

        Process? process = null;
        var launched = false;
        var requestSent = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process = StartElevatedHelper(pipeName, secret);
            launched = true;

            // Once Windows starts the elevated helper, caller cancellation must not abandon or
            // terminate the mutation. Only finite broker-owned timeouts remain in effect.
            using var connectionCts = new CancellationTokenSource(_connectionTimeout);
            await WaitForConnectionOrHelperExitAsync(
                pipe,
                process,
                connectionCts.Token).ConfigureAwait(false);
            VerifyClientProcess(pipe.SafePipeHandle, process.Id);
            await PackageAdminPipeProtocol.AuthenticateServerAsync(
                pipe,
                pipeName,
                secret,
                connectionCts.Token).ConfigureAwait(false);
            await PackageAdminPipeProtocol.WriteJsonAsync(
                pipe,
                request,
                connectionCts.Token).ConfigureAwait(false);
            requestSent = true;
            Report(
                progress,
                operation.Id,
                operation.Kind switch
                {
                    PackageOperationKind.Install => PackageOperationState.Installing,
                    PackageOperationKind.Upgrade => PackageOperationState.Upgrading,
                    PackageOperationKind.Uninstall => PackageOperationState.Uninstalling,
                    _ => PackageOperationState.Resolving
                },
                "The elevated package helper is running. Progress is indeterminate and the " +
                    "operation can no longer be canceled safely.",
                cancellationSupported: false);

            using var operationCts = new CancellationTokenSource(_operationTimeout);
            var response = await PackageAdminPipeProtocol.ReadJsonAsync<PrivilegedPackageResponse>(
                pipe,
                operationCts.Token).ConfigureAwait(false);

            if (!IsValidResponse(response, request))
            {
                return OutcomeUnknown(operation);
            }

            return response.Result;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return Failure(
                operation,
                WingetErrorKind.ElevationDenied,
                "ElevationCancelled",
                "Administrator approval was canceled.",
                ranAsAdministrator: false,
                exception.HResult);
        }
        catch (OperationCanceledException) when (!launched && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            if (requestSent)
            {
                return OutcomeUnknown(operation, exception.HResult);
            }

            return Failure(
                operation,
                WingetErrorKind.ComFailure,
                "PackageAdminTimeout",
                "The elevated package helper did not respond in time. Review Activity before retrying.",
                ranAsAdministrator: launched,
                exception.HResult);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (requestSent)
            {
                return OutcomeUnknown(operation, exception.HResult);
            }

            return Failure(
                operation,
                WingetErrorKind.ComFailure,
                "PackageAdminAuthenticationFailed",
                "The helper must run under the same Windows account. Alternate administrator " +
                    "credentials are rejected to avoid mutating another user's package profile.",
                ranAsAdministrator: launched,
                exception.HResult);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            if (requestSent)
            {
                return OutcomeUnknown(operation, exception.HResult);
            }

            return Failure(
                operation,
                WingetErrorKind.ComFailure,
                "PackageAdminFailed",
                "The elevated package helper could not complete the request.",
                ranAsAdministrator: launched,
                exception.HResult);
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static bool TryCreateRequest(
        PackageOperation operation,
        out PrivilegedPackageRequest request,
        out string error)
    {
        request = new PrivilegedPackageRequest();
        error = string.Empty;
        if (!operation.RunAsAdministrator
            || operation.Target is not WingetTarget target
            || operation.Package.IsEmpty
            || target.Package.IsEmpty
            || operation.Package != target.Package)
        {
            error = "Only an explicitly approved, exact WinGet package operation can be elevated.";
            return false;
        }

        try
        {
            request = PrivilegedPackageRequest.FromOperation(operation);
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }

        var validation = PrivilegedPackageRequestValidator.Validate(request);
        if (validation.IsValid)
        {
            return true;
        }

        error = string.Join(" ", validation.Errors);
        return false;
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
                "Windows did not start the package-administration helper.");
    }

    internal static bool IsValidResponse(
        PrivilegedPackageResponse response,
        PrivilegedPackageRequest request)
    {
        if (response.SchemaVersion != PrivilegedPackageRequest.CurrentSchemaVersion
            || response.RequestId != request.RequestId
            || response.Result is null
            || response.Result.OperationId != request.RequestId
            || response.Result.Kind != request.Kind
            || !response.Result.RanAsAdministrator
            || !response.Result.AdministratorRetryRequested
            || response.Result.State is PackageOperationState.Queued
                or PackageOperationState.Resolving
                or PackageOperationState.Downloading
                or PackageOperationState.Installing
                or PackageOperationState.Upgrading
                or PackageOperationState.Uninstalling)
        {
            return false;
        }

        return response.Result.EffectiveTarget is WingetTarget target
            && string.Equals(response.Result.Package.Id, request.PackageId, StringComparison.Ordinal)
            && string.Equals(response.Result.Package.SourceId, request.SourceId, StringComparison.Ordinal)
            && string.Equals(target.Package.Id, request.PackageId, StringComparison.Ordinal)
            && string.Equals(target.Package.SourceId, request.SourceId, StringComparison.Ordinal);
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
                "An unexpected process connected to the package helper pipe.");
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
                "The elevated helper exited before authenticating the package pipe.");
        }

        await connectionTask.ConfigureAwait(false);
    }

    private static void Report(
        IProgress<OperationProgress>? progress,
        Guid operationId,
        PackageOperationState state,
        string message,
        bool cancellationSupported) =>
        progress?.Report(new OperationProgress
        {
            OperationId = operationId,
            State = state,
            Message = message,
            CancellationSupported = cancellationSupported
        });

    private static OperationResult Failure(
        PackageOperation operation,
        WingetErrorKind kind,
        string code,
        string message,
        bool ranAsAdministrator,
        int? hresult = null) =>
        new()
        {
            OperationId = operation.Id,
            Package = operation.Package,
            Target = operation.EffectiveTarget,
            Kind = operation.Kind,
            State = PackageOperationState.Failed,
            StartedAt = operation.EnqueuedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            RanAsAdministrator = ranAsAdministrator,
            AdministratorRetryRequested = operation.RunAsAdministrator,
            Error = new WingetError
            {
                Kind = kind,
                Code = code,
                Message = message,
                HResult = hresult
            }
        };

    private static OperationResult OutcomeUnknown(
        PackageOperation operation,
        int? hresult = null) =>
        Failure(
            operation,
            WingetErrorKind.OutcomeUnknown,
            OutcomeUnknownCode,
            OutcomeUnknownMessage,
            ranAsAdministrator: true,
            hresult: hresult);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}
