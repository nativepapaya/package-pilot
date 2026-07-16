using System.Text;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Lazily reads GUID-correlated WinGet diagnostics and exact Windows deployment events. No
/// directory is scanned and no event log is opened until the user clicks an Activity button.
/// </summary>
public sealed class WindowsOperationDiagnosticsService : IOperationDiagnosticsService
{
    private const string SensitivityNotice =
        "Logs can contain local paths, package metadata, and source addresses. Recognized " +
        "credentials and the user-profile path are redacted, but review the text before sharing it.";

    private readonly string _ownedInstallerLogRoot;
    private readonly string _trustedOwnedRoot;
    private readonly string _wingetDiagnosticsRoot;
    private readonly IWindowsDeploymentEventLogReader _deploymentEventLogReader;

    public WindowsOperationDiagnosticsService(
        string ownedInstallerLogRoot,
        string trustedOwnedRoot)
        : this(
            ownedInstallerLogRoot,
            GetDefaultWingetDiagnosticsRoot(),
            new WindowsDeploymentEventLogReader(),
            trustedOwnedRoot)
    {
    }

    internal WindowsOperationDiagnosticsService(
        string ownedInstallerLogRoot,
        string wingetDiagnosticsRoot,
        IWindowsDeploymentEventLogReader deploymentEventLogReader,
        string? trustedOwnedRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedInstallerLogRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(wingetDiagnosticsRoot);
        _ownedInstallerLogRoot = Path.GetFullPath(ownedInstallerLogRoot);
        _trustedOwnedRoot = Path.GetFullPath(
            trustedOwnedRoot
            ?? Path.GetDirectoryName(_ownedInstallerLogRoot)
            ?? throw new ArgumentException(
                "The app-owned diagnostic root must have a parent.",
                nameof(ownedInstallerLogRoot)));
        _wingetDiagnosticsRoot = Path.GetFullPath(wingetDiagnosticsRoot);
        _deploymentEventLogReader = deploymentEventLogReader
            ?? throw new ArgumentNullException(nameof(deploymentEventLogReader));
    }

    internal bool TryPrepareWingetInstallerLog(Guid operationId, out string logPath) =>
        OperationDiagnosticFiles.TryPrepareInstallerLog(
            _trustedOwnedRoot,
            _ownedInstallerLogRoot,
            operationId,
            out logPath);

    internal Task FinalizeWingetInstallerLogAsync(Guid operationId) =>
        OperationDiagnosticFiles.FinalizeInstallerLogAsync(
            _trustedOwnedRoot,
            _ownedInstallerLogRoot,
            operationId);

    public Task<OperationDiagnosticDocument> ReadAsync(
        OperationResult operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var diagnostic = operation.EffectiveDiagnostic;
        if (diagnostic is null)
        {
            return Task.FromResult(Unavailable(
                "Operation diagnostics",
                "No diagnostic reference was recorded for this activity."));
        }

        return diagnostic.Provider switch
        {
            OperationDiagnosticProvider.Winget
                when diagnostic.ReferenceId == operation.OperationId =>
                ReadWingetAsync(operation, diagnostic, cancellationToken),
            OperationDiagnosticProvider.WindowsDeployment =>
                ReadWindowsDeploymentAsync(operation, diagnostic, cancellationToken),
            _ => Task.FromResult(Unavailable(
                "Operation diagnostics",
                "The diagnostic reference is invalid or uses an unsupported provider."))
        };
    }

    public Task DeleteOwnedLogsAsync(
        IReadOnlyCollection<OperationDiagnosticReference> diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return OperationDiagnosticFiles.DeleteOwnedLogsAsync(
            _trustedOwnedRoot,
            _ownedInstallerLogRoot,
            diagnostics
                .Where(reference => reference.Provider == OperationDiagnosticProvider.Winget)
                .Select(reference => reference.ReferenceId),
            cancellationToken);
    }

    private async Task<OperationDiagnosticDocument> ReadWingetAsync(
        OperationResult operation,
        OperationDiagnosticReference diagnostic,
        CancellationToken cancellationToken)
    {
        var wingetLogTask = OperationDiagnosticFiles.FindWingetComLogAsync(
            _wingetDiagnosticsRoot,
            diagnostic.ReferenceId,
            operation.StartedAt,
            operation.CompletedAt,
            cancellationToken);
        var installerLogTask = OperationDiagnosticFiles.ReadInstallerLogAsync(
            _trustedOwnedRoot,
            _ownedInstallerLogRoot,
            diagnostic.ReferenceId,
            cancellationToken);
        await Task.WhenAll(wingetLogTask, installerLogTask).ConfigureAwait(false);

        var wingetLog = await wingetLogTask.ConfigureAwait(false);
        var installerLog = await installerLogTask.ConfigureAwait(false);
        var isStore = IsMicrosoftStoreSource(operation.Package.SourceId);
        var providerLabel = isStore ? "WinGet / Microsoft Store" : "WinGet";
        var builder = CreateOperationHeader(operation, providerLabel, diagnostic.ReferenceId);

        if (wingetLog is not null)
        {
            AppendLogSection(
                builder,
                $"Correlated WinGet diagnostic file ({wingetLog.FileName})",
                wingetLog.Content);
        }

        if (installerLog is not null)
        {
            AppendLogSection(
                builder,
                "Installer log (requested through WinGet)",
                installerLog);
        }

        var notice = GetWingetNotice(isStore, wingetLog is not null, installerLog is not null);
        return new OperationDiagnosticDocument
        {
            Title = isStore ? "WinGet / Microsoft Store diagnostics" : "WinGet operation diagnostics",
            ProviderLabel = providerLabel,
            Text = OperationDiagnosticRedactor.Redact(builder.ToString()),
            Notice = notice,
            IsTruncated = wingetLog?.Content.IsTruncated == true || installerLog?.IsTruncated == true,
            HasProviderLog = wingetLog is not null,
            HasInstallerLog = installerLog is not null
        };
    }

    private async Task<OperationDiagnosticDocument> ReadWindowsDeploymentAsync(
        OperationResult operation,
        OperationDiagnosticReference diagnostic,
        CancellationToken cancellationToken)
    {
        if (diagnostic.ReferenceId == Guid.Empty)
        {
            return Unavailable(
                "Windows deployment diagnostics",
                "Windows did not return a deployment activity identifier for this operation.");
        }

        var events = await _deploymentEventLogReader
            .ReadAsync(diagnostic.ReferenceId, cancellationToken)
            .ConfigureAwait(false);
        var builder = CreateOperationHeader(
            operation,
            "Windows package deployment (MSIX)",
            diagnostic.ReferenceId);
        builder.AppendLine($"Event channel: {events.ChannelName}");
        builder.AppendLine($"Deployment events: {events.EventCount}");

        if (events.HasEvents)
        {
            builder.AppendLine();
            builder.AppendLine("--- Windows deployment event log ---");
            builder.Append(events.Text);
        }

        var notice = events.HasEvents
            ? events.IsComplete
                ? SensitivityNotice
                : $"{events.UnavailableMessage ?? "Only part of the deployment log could be read."} {SensitivityNotice}"
            : $"{events.UnavailableMessage ?? "No retained Windows deployment events were found for this exact activity ID."} {SensitivityNotice}";
        return new OperationDiagnosticDocument
        {
            Title = "Windows deployment diagnostics",
            ProviderLabel = "Windows package deployment (MSIX)",
            Text = OperationDiagnosticRedactor.Redact(builder.ToString()),
            Notice = notice,
            IsTruncated = events.IsTruncated,
            HasProviderLog = events.HasEvents
        };
    }

    private static StringBuilder CreateOperationHeader(
        OperationResult operation,
        string providerLabel,
        Guid referenceId)
    {
        var packageId = operation.EffectiveTarget?.Id ?? operation.Package.Id;
        var builder = new StringBuilder();
        builder.AppendLine("Package Pilot operation diagnostics");
        builder.AppendLine($"Provider: {providerLabel}");
        builder.AppendLine($"Package: {packageId}");
        if (!string.IsNullOrWhiteSpace(operation.Package.SourceId))
        {
            builder.AppendLine($"Source: {operation.Package.SourceId}");
        }

        builder.AppendLine($"Action: {operation.Kind}");
        builder.AppendLine($"Status: {operation.State}");
        builder.AppendLine($"Operation ID: {operation.OperationId:D}");
        builder.AppendLine($"Provider reference: {referenceId:D}");
        builder.AppendLine($"Started: {operation.StartedAt:O}");
        builder.AppendLine($"Completed: {operation.CompletedAt:O}");
        if (operation.Error is { } error)
        {
            builder.AppendLine($"Result code: {error.Code}");
            if (error.HResult is { } hresult)
            {
                builder.AppendLine($"HRESULT: 0x{hresult:X8}");
            }

            builder.AppendLine($"Result message: {error.Message}");
        }

        return builder;
    }

    private static void AppendLogSection(
        StringBuilder builder,
        string heading,
        BoundedLogText log)
    {
        builder.AppendLine();
        builder.AppendLine($"--- {heading} ---");
        if (log.IsTruncated)
        {
            builder.AppendLine("[Earlier content omitted to keep this viewer bounded.]");
        }

        builder.Append(log.Text);
    }

    private static string GetWingetNotice(
        bool isStore,
        bool hasWingetLog,
        bool hasInstallerLog)
    {
        var availability = (hasWingetLog, hasInstallerLog) switch
        {
            (true, true) => "The correlated WinGet log and installer-native log are shown.",
            (true, false) when isStore =>
                "The correlated WinGet Store-handoff log is shown. Microsoft Store does not expose a Store-owned log path through WinGet.",
            (true, false) =>
                "The correlated WinGet log is shown. This installer did not produce a separate installer-native log.",
            (false, true) =>
                "The installer-native log is shown. The correlated WinGet trace has expired or is unavailable.",
            _ when isStore =>
                "No retained WinGet handoff log was found. Microsoft Store does not expose a Store-owned per-operation log through WinGet.",
            _ =>
                "No retained WinGet or installer log was found. WinGet diagnostics expire, and some installer types do not produce installer-native logs."
        };

        var scopeNotice = hasWingetLog
            ? "The WinGet file was matched by the exact operation GUID, but it can contain surrounding WinGet COM context. "
            : string.Empty;
        return $"{availability} {scopeNotice}{SensitivityNotice}";
    }

    private static OperationDiagnosticDocument Unavailable(string title, string message) => new()
    {
        Title = title,
        ProviderLabel = "Unavailable",
        Text = message,
        Notice = SensitivityNotice
    };

    private static bool IsMicrosoftStoreSource(string sourceId) =>
        string.Equals(sourceId, "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceId, "StoreEdgeFD", StringComparison.OrdinalIgnoreCase);

    private static string GetDefaultWingetDiagnosticsRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
            "LocalState",
            "DiagOutputDir");
}
