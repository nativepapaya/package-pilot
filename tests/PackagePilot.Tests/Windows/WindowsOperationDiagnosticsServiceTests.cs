using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Windows;

public sealed class WindowsOperationDiagnosticsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PackagePilot.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WingetRead_RequiresExactCorrelationAndRedactsSensitiveValues()
    {
        var operation = WingetResult("winget");
        var owned = Path.Combine(_root, "owned");
        var winget = Path.Combine(_root, "winget");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(winget);
        await File.WriteAllTextAsync(
            Path.Combine(winget, "WinGetCOM-unrelated.log"),
            "CorrelationData: {\"operationId\":\"00000000-0000-0000-0000-000000000001\"}\nunrelated");
        await File.WriteAllTextAsync(
            Path.Combine(winget, "WinGetCOM-matching.log"),
            $"CorrelationData: {{\"operationId\":\"{operation.OperationId:D}\"}}\n" +
            "Authorization: Bearer secret-value\nhttps://example.test/?token=signed-value\n" +
            "{\"client_secret\":\"json-secret\"}\n<Data Name='Password'>xml-secret</Data>\n" +
            "Cookie: session=cookie-secret; csrf=second-cookie-secret\n" +
            "https://user:uri-secret@example.test/package\n" +
            "installer.exe --token command-secret\nwinget detail");
        await File.WriteAllTextAsync(
            OperationDiagnosticFiles.GetInstallerLogPath(owned, operation.OperationId),
            $"Installer path: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\\Downloads\ninstaller detail");
        var service = Service(owned, winget);

        var document = await service.ReadAsync(operation);

        Assert.True(document.HasProviderLog);
        Assert.True(document.HasInstallerLog);
        Assert.Contains("winget detail", document.Text);
        Assert.Contains("installer detail", document.Text);
        Assert.DoesNotContain("unrelated", document.Text);
        Assert.DoesNotContain("secret-value", document.Text);
        Assert.DoesNotContain("signed-value", document.Text);
        Assert.DoesNotContain("json-secret", document.Text);
        Assert.DoesNotContain("xml-secret", document.Text);
        Assert.DoesNotContain("command-secret", document.Text);
        Assert.DoesNotContain("cookie-secret", document.Text);
        Assert.DoesNotContain("second-cookie-secret", document.Text);
        Assert.DoesNotContain("uri-secret", document.Text);
        Assert.Contains("[REDACTED]", document.Text);
        Assert.Contains("%USERPROFILE%", document.Text);
        Assert.Contains("surrounding WinGet COM context", document.Notice);
    }

    [Fact]
    public async Task WingetRead_DoesNotAssociateAnUnrelatedGlobalLog()
    {
        var operation = WingetResult("winget");
        var owned = Path.Combine(_root, "owned");
        var winget = Path.Combine(_root, "winget");
        Directory.CreateDirectory(winget);
        await File.WriteAllTextAsync(
            Path.Combine(winget, "WinGetCOM-other.log"),
            $"operationId: 00000000-0000-0000-0000-000000000001; package text mentions {operation.OperationId:D}.");

        var document = await Service(owned, winget).ReadAsync(operation);

        Assert.False(document.HasProviderLog);
        Assert.False(document.HasInstallerLog);
        Assert.DoesNotContain("Package text mentions", document.Text);
        Assert.Contains("No retained WinGet or installer log", document.Notice);
    }

    [Fact]
    public async Task WingetRead_TailsOversizedLogsAndReportsTruncation()
    {
        var operation = WingetResult("winget");
        var owned = Path.Combine(_root, "owned");
        var winget = Path.Combine(_root, "winget");
        Directory.CreateDirectory(winget);
        var content = $"CorrelationData: {{\"operationId\":\"{operation.OperationId:D}\"}}\nBEGIN\n"
            + new string('x', OperationDiagnosticFiles.MaximumDisplayedBytesPerLog)
            + "\nEND";
        await File.WriteAllTextAsync(Path.Combine(winget, "WinGetCOM-large.log"), content);

        var document = await Service(owned, winget).ReadAsync(operation);

        Assert.True(document.IsTruncated);
        Assert.Contains("END", document.Text);
        Assert.DoesNotContain("BEGIN", document.Text);
    }

    [Theory]
    [InlineData("msstore")]
    [InlineData("StoreEdgeFD")]
    public async Task StoreBackedWingetRead_LabelsTheHandoffWithoutClaimingStoreLogs(string sourceId)
    {
        var operation = WingetResult(sourceId);
        var winget = Path.Combine(_root, "winget");
        Directory.CreateDirectory(winget);
        await File.WriteAllTextAsync(
            Path.Combine(winget, "WinGetCOM-store.log"),
            $"CorrelationData: {{\"operationId\":\"{operation.OperationId:D}\"}}\nStore handoff");

        var document = await Service(Path.Combine(_root, "owned"), winget).ReadAsync(operation);

        Assert.Equal("WinGet / Microsoft Store", document.ProviderLabel);
        Assert.Contains("Store-handoff", document.Notice);
        Assert.Contains("does not expose a Store-owned log path", document.Notice);
    }

    [Fact]
    public async Task WindowsDeploymentRead_UsesOnlyTheExactActivityId()
    {
        var activityId = Guid.NewGuid();
        var eventReader = new FakeDeploymentEventLogReader
        {
            Result = new WindowsDeploymentEventLogResult(
                activityId,
                WindowsDeploymentEventLogReader.ChannelName,
                "deployment event",
                EventCount: 1,
                IsTruncated: false,
                IsComplete: true,
                NativeErrorCode: null,
                UnavailableMessage: null)
        };
        var operation = new OperationResult
        {
            OperationId = Guid.NewGuid(),
            Target = new MsixTarget
            {
                PackageFullName = "Contoso.App_1.0.0.0_x64__publisher",
                PackageFamilyName = "Contoso.App_publisher"
            },
            Kind = PackageOperationKind.Uninstall,
            State = PackageOperationState.Completed,
            Diagnostic = new OperationDiagnosticReference
            {
                Provider = OperationDiagnosticProvider.WindowsDeployment,
                ReferenceId = activityId
            }
        };
        var service = new WindowsOperationDiagnosticsService(
            Path.Combine(_root, "owned"),
            Path.Combine(_root, "winget"),
            eventReader);

        var document = await service.ReadAsync(operation);

        Assert.Equal(activityId, eventReader.RequestedActivityId);
        Assert.True(document.HasProviderLog);
        Assert.Contains("deployment event", document.Text);
    }

    [Fact]
    public async Task DeleteOwnedLogs_DeletesOnlyExactAppOwnedWingetFiles()
    {
        var operation = WingetResult("winget");
        var owned = Path.Combine(_root, "owned");
        var winget = Path.Combine(_root, "winget");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(winget);
        var ownedPath = OperationDiagnosticFiles.GetInstallerLogPath(owned, operation.OperationId);
        var providerPath = Path.Combine(winget, "WinGetCOM-retained.log");
        await File.WriteAllTextAsync(ownedPath, "owned");
        await File.WriteAllTextAsync(providerPath, "provider-owned");

        await Service(owned, winget).DeleteOwnedLogsAsync([operation.Diagnostic!]);

        Assert.False(File.Exists(ownedPath));
        Assert.True(File.Exists(providerPath));
    }

    [Fact]
    public async Task FinalizeInstallerLog_ReplacesAnOversizedFileWithASafetyMarker()
    {
        var operationId = Guid.NewGuid();
        var owned = Path.Combine(_root, "owned");
        Directory.CreateDirectory(owned);
        var path = OperationDiagnosticFiles.GetInstallerLogPath(owned, operationId);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(OperationDiagnosticFiles.MaximumIndividualOwnedLogBytes + 1);
        }
        var service = Service(owned, Path.Combine(_root, "winget"));

        await service.FinalizeWingetInstallerLogAsync(operationId);

        var marker = await File.ReadAllTextAsync(path);
        Assert.Contains("exceeded Package Pilot's 16 MB safety limit", marker);
        Assert.True(new FileInfo(path).Length < 1024);
    }

    [Fact]
    public async Task FinalizeInstallerLog_RetainsAtMostTheHistoryLimit()
    {
        var owned = Path.Combine(_root, "owned");
        Directory.CreateDirectory(owned);
        foreach (var index in Enumerable.Range(0, OperationDiagnosticFiles.MaximumOwnedLogCount + 1))
        {
            var path = OperationDiagnosticFiles.GetInstallerLogPath(owned, Guid.CreateVersion7());
            await File.WriteAllTextAsync(path, index.ToString());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(index));
        }

        await Service(owned, Path.Combine(_root, "winget"))
            .FinalizeWingetInstallerLogAsync(Guid.NewGuid());

        Assert.Equal(
            OperationDiagnosticFiles.MaximumOwnedLogCount,
            Directory.EnumerateFiles(owned, "*.log").Count());
    }

    [Fact]
    public async Task DeleteOwnedLogs_ReportsALockedFileAfterAttemptingCleanup()
    {
        var operation = WingetResult("winget");
        var owned = Path.Combine(_root, "owned");
        Directory.CreateDirectory(owned);
        var path = OperationDiagnosticFiles.GetInstallerLogPath(owned, operation.OperationId);
        await File.WriteAllTextAsync(path, "locked");
        await using var locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        await Assert.ThrowsAsync<IOException>(() =>
            Service(owned, Path.Combine(_root, "winget"))
                .DeleteOwnedLogsAsync([operation.Diagnostic!]));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void LogDiscoveryLimits_AreHardBounded()
    {
        Assert.Equal(32, OperationDiagnosticFiles.MaximumCorrelationCandidates);
        Assert.Equal(512, OperationDiagnosticFiles.MaximumEnumeratedLogFiles);
        Assert.Equal(100, OperationDiagnosticFiles.MaximumOwnedLogCount);
    }

    [Fact]
    public void PrepareInstallerLog_VerifiesWriteAccessWithoutLeavingAProbe()
    {
        Directory.CreateDirectory(_root);
        var owned = Path.Combine(_root, "owned");
        var service = new WindowsOperationDiagnosticsService(
            owned,
            Path.Combine(_root, "winget"),
            new FakeDeploymentEventLogReader(),
            _root);

        var prepared = service.TryPrepareWingetInstallerLog(Guid.NewGuid(), out var logPath);

        Assert.True(prepared);
        Assert.StartsWith(owned, logPath, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(owned, ".write-probe-*.tmp"));
    }

    [Fact]
    public void PrepareInstallerLog_RejectsAPathOutsideTheTrustedLocalRoot()
    {
        var trusted = Path.Combine(_root, "trusted");
        var outside = Path.Combine(_root, "outside", "installer-logs");
        Directory.CreateDirectory(trusted);
        var service = new WindowsOperationDiagnosticsService(
            outside,
            Path.Combine(_root, "winget"),
            new FakeDeploymentEventLogReader(),
            trusted);

        var prepared = service.TryPrepareWingetInstallerLog(Guid.NewGuid(), out _);

        Assert.False(prepared);
        Assert.False(Directory.Exists(outside));
    }

    [Fact]
    public void PrepareInstallerLog_FailsClosedWhenAnAncestorIsNotADirectory()
    {
        Directory.CreateDirectory(_root);
        var blockingPath = Path.Combine(_root, "operation-diagnostics");
        File.WriteAllText(blockingPath, "not a directory");
        var service = new WindowsOperationDiagnosticsService(
            Path.Combine(blockingPath, "installer-logs"),
            Path.Combine(_root, "winget"),
            new FakeDeploymentEventLogReader(),
            _root);

        Assert.False(service.TryPrepareWingetInstallerLog(Guid.NewGuid(), out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WindowsOperationDiagnosticsService Service(string owned, string winget) =>
        new(owned, winget, new FakeDeploymentEventLogReader());

    private static OperationResult WingetResult(string sourceId)
    {
        var operationId = Guid.NewGuid();
        var package = new PackageKey("Contoso.App", sourceId);
        return new OperationResult
        {
            OperationId = operationId,
            Package = package,
            Target = new WingetTarget { Package = package },
            Kind = PackageOperationKind.Upgrade,
            State = PackageOperationState.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Diagnostic = new OperationDiagnosticReference
            {
                Provider = OperationDiagnosticProvider.Winget,
                ReferenceId = operationId
            }
        };
    }

    private sealed class FakeDeploymentEventLogReader : IWindowsDeploymentEventLogReader
    {
        public Guid RequestedActivityId { get; private set; }
        public WindowsDeploymentEventLogResult Result { get; init; } = new(
            Guid.Empty,
            WindowsDeploymentEventLogReader.ChannelName,
            string.Empty,
            EventCount: 0,
            IsTruncated: false,
            IsComplete: true,
            NativeErrorCode: null,
            UnavailableMessage: "No events.");

        public Task<WindowsDeploymentEventLogResult> ReadAsync(
            Guid activityId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedActivityId = activityId;
            return Task.FromResult(Result);
        }
    }
}
