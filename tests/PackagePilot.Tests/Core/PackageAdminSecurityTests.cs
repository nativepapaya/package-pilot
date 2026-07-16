using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Core;

public sealed class PackageAdminSecurityTests
{
    private const string PackageFingerprint =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string SourceFingerprint =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void PackageAgreementFingerprint_IsStableAndInvalidatedByExactChanges()
    {
        var package = new PackageKey("Contoso.App", "winget");
        var agreement = new PackageAgreement
        {
            Id = "package:Contoso.App:0",
            Kind = AgreementKind.Package,
            Label = "Terms",
            Text = "Exact terms",
            AgreementUri = new Uri("https://contoso.test/terms"),
            RequiresExplicitAcceptance = true
        };

        var first = PackageAgreementSnapshot.Create(package, [agreement]);
        var repeated = PackageAgreementSnapshot.Create(package, [agreement]);
        var changed = PackageAgreementSnapshot.Create(
            package,
            [agreement with { Text = "Changed terms" }]);

        Assert.Matches("^[A-F0-9]{64}$", first.Fingerprint);
        Assert.Equal(first.Fingerprint, repeated.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        Assert.True(first.Matches(repeated.Fingerprint));
        Assert.False(first.Matches(changed.Fingerprint));
    }

    [Fact]
    public void Validator_AllowsOnlyExactSourceQualifiedWinGetMutationData()
    {
        var request = ValidInstallRequest();

        var result = PrivilegedPackageRequestValidator.Validate(request);

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        var operation = request.ToOperation();
        var target = Assert.IsType<WingetTarget>(operation.EffectiveTarget);
        Assert.Equal(new PackageKey("Contoso.App", "winget"), target.Package);
        Assert.True(operation.RunAsAdministrator);
        Assert.True(operation.Preferences.AllowElevation);
    }

    [Theory]
    [InlineData("Contoso.App\r\n", "winget")]
    [InlineData("C:\\Windows\\System32", "winget")]
    [InlineData("--silent", "winget")]
    [InlineData("Contoso.App", "https://source.test/index")]
    [InlineData("Contoso.App", "")]
    public void Validator_RejectsControlCharactersPathsArgumentsAndMissingSources(
        string packageId,
        string sourceId)
    {
        var validation = PrivilegedPackageRequestValidator.Validate(
            ValidInstallRequest() with { PackageId = packageId, SourceId = sourceId });

        Assert.False(validation.IsValid);
    }

    [Fact]
    public void Validator_RejectsInvalidEnumsAndAgreementFingerprints()
    {
        var request = ValidInstallRequest() with
        {
            Kind = (PackageOperationKind)999,
            Scope = (InstallerScope)999,
            Architecture = (PackageArchitecture)999,
            AcceptedSourceAgreementFingerprint = "not-hex",
            AcceptedPackageAgreementFingerprint = new string('A', 63)
        };

        var validation = PrivilegedPackageRequestValidator.Validate(request);

        Assert.False(validation.IsValid);
        Assert.True(validation.Errors.Count >= 5);
    }

    [Fact]
    public void Validator_RejectsAllAgreementChannelsOnUninstall()
    {
        var request = ValidInstallRequest() with
        {
            Kind = PackageOperationKind.Uninstall,
            Architecture = PackageArchitecture.Unknown,
            Locale = null
        };

        var validation = PrivilegedPackageRequestValidator.Validate(request);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("install-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispatcher_InvokesExactlyOneAllowlistedMutation()
    {
        var client = new RecordingWingetClient();
        var request = ValidInstallRequest();

        var result = await PrivilegedPackageRequestDispatcher.DispatchAsync(client, request);

        Assert.Equal(1, client.InstallCalls);
        Assert.Equal(0, client.UpgradeCalls);
        Assert.Equal(0, client.UninstallCalls);
        Assert.True(result.RanAsAdministrator);
        Assert.True(result.AdministratorRetryRequested);
        Assert.True(client.LastOperation!.RunAsAdministrator);
        Assert.Equal(request.RequestId, client.LastOperation.Id);
    }

    [Fact]
    public async Task StrictJson_RejectsCommandsArgumentsPathsAndHeaders()
    {
        var json = JsonSerializer.Serialize(
            ValidInstallRequest(),
            PackageAdminPipeProtocol.CreateSerializerOptions());
        json = json[..^1]
            + ",\"command\":\"winget.exe\",\"arguments\":[\"--silent\"],"
            + "\"path\":\"C:\\\\Windows\",\"headers\":{\"authorization\":\"secret\"}}";
        using var stream = CreateFramedStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<JsonException>(() =>
            PackageAdminPipeProtocol.ReadJsonAsync<PrivilegedPackageRequest>(stream));
    }

    [Fact]
    public void PackageAndSourcePipeDomainsAreDistinct()
    {
        var packagePipe = PackageAdminPipeProtocol.CreatePipeName();
        var sourcePipe = SourceAdminPipeProtocol.CreatePipeName();

        Assert.NotEqual(PackageAdminPipeProtocol.PipeNamePrefix, SourceAdminPipeProtocol.PipeNamePrefix);
        Assert.True(PackageAdminPipeProtocol.IsValidPipeName(packagePipe));
        Assert.False(SourceAdminPipeProtocol.IsValidPipeName(packagePipe));
        Assert.True(SourceAdminPipeProtocol.IsValidPipeName(sourcePipe));
        Assert.False(PackageAdminPipeProtocol.IsValidPipeName(sourcePipe));
    }

    [Fact]
    public async Task AuthenticatedPackagePipe_ExchangesExactlyOneTypedRequestAndResponse()
    {
        var pipeName = PackageAdminPipeProtocol.CreatePipeName();
        var secret = PackageAdminPipeProtocol.CreateSecret();
        var request = ValidInstallRequest();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = ElevatedPipeAclFactory.CreateServerForCurrentUser(pipeName);

        var clientTask = Task.Run(async () =>
        {
            using var client = ElevatedPipeAclFactory.CreateClient(pipeName);
            await client.ConnectAsync(timeout.Token);
            await PackageAdminPipeProtocol.AuthenticateClientAsync(
                client,
                pipeName,
                secret,
                timeout.Token);
            var received = await PackageAdminPipeProtocol.ReadJsonAsync<PrivilegedPackageRequest>(
                client,
                timeout.Token);
            await PackageAdminPipeProtocol.WriteJsonAsync(
                client,
                new PrivilegedPackageResponse
                {
                    RequestId = received.RequestId,
                    Result = new OperationResult
                    {
                        OperationId = received.RequestId,
                        Package = new PackageKey(received.PackageId, received.SourceId),
                        Target = new WingetTarget
                        {
                            Package = new PackageKey(received.PackageId, received.SourceId)
                        },
                        Kind = received.Kind,
                        State = PackageOperationState.Completed,
                        RanAsAdministrator = true,
                        AdministratorRetryRequested = true
                    }
                },
                timeout.Token);
        }, timeout.Token);

        await server.WaitForConnectionAsync(timeout.Token);
        await PackageAdminPipeProtocol.AuthenticateServerAsync(
            server,
            pipeName,
            secret,
            timeout.Token);
        await PackageAdminPipeProtocol.WriteJsonAsync(server, request, timeout.Token);
        var response = await PackageAdminPipeProtocol.ReadJsonAsync<PrivilegedPackageResponse>(
            server,
            timeout.Token);
        await clientTask;

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.True(response.Result.RanAsAdministrator);
    }

    [Fact]
    public async Task PackageAuthentication_RejectsWrongOneShotSecret()
    {
        var pipeName = PackageAdminPipeProtocol.CreatePipeName();
        var serverSecret = PackageAdminPipeProtocol.CreateSecret();
        var clientSecret = PackageAdminPipeProtocol.CreateSecret();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = ElevatedPipeAclFactory.CreateServerForCurrentUser(pipeName);

        var clientTask = Task.Run(async () =>
        {
            using var client = ElevatedPipeAclFactory.CreateClient(pipeName);
            await client.ConnectAsync(timeout.Token);
            await PackageAdminPipeProtocol.AuthenticateClientAsync(
                client,
                pipeName,
                clientSecret,
                timeout.Token);
        }, timeout.Token);

        await server.WaitForConnectionAsync(timeout.Token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            PackageAdminPipeProtocol.AuthenticateServerAsync(
                server,
                pipeName,
                serverSecret,
                timeout.Token));
        await clientTask;
    }

    private static PrivilegedPackageRequest ValidInstallRequest() => new()
    {
        RequestId = Guid.NewGuid(),
        Kind = PackageOperationKind.Install,
        PackageId = "Contoso.App",
        SourceId = "winget",
        Scope = InstallerScope.Machine,
        Architecture = PackageArchitecture.X64,
        Locale = "en-US",
        AcceptSourceAgreements = true,
        AcceptedSourceAgreementFingerprint = SourceFingerprint,
        AcceptPackageAgreements = true,
        AcceptedPackageAgreementFingerprint = PackageFingerprint
    };

    private static MemoryStream CreateFramedStream(byte[] payload)
    {
        var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }

    private sealed class RecordingWingetClient : IWingetClient
    {
        public int InstallCalls { get; private set; }
        public int UpgradeCalls { get; private set; }
        public int UninstallCalls { get; private set; }
        public PackageOperation? LastOperation { get; private set; }

        public Task<OperationResult> InstallAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InstallCalls++;
            LastOperation = operation;
            return Task.FromResult(Success(operation));
        }

        public Task<OperationResult> UpgradeAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UpgradeCalls++;
            LastOperation = operation;
            return Task.FromResult(Success(operation));
        }

        public Task<OperationResult> UninstallAsync(
            PackageOperation operation,
            IProgress<OperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UninstallCalls++;
            LastOperation = operation;
            return Task.FromResult(Success(operation));
        }

        public Task<WingetCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WingetCapabilities.Unavailable("unused"));

        public Task<PackageSearchResult> SearchAsync(
            PackageQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PackageSearchResult());

        public Task<IReadOnlyList<PackageSourceStatus>> GetSourcesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSourceStatus>>([]);

        public Task<IReadOnlyList<PackageSummary>> GetInstalledPackagesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSummary>>([]);

        public Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PackageSummary>>([]);

        public Task<PackageDetails?> GetPackageDetailsAsync(
            PackageKey package,
            InstallPreferences? preferences = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PackageDetails?>(null);

        private static OperationResult Success(PackageOperation operation) => new()
        {
            OperationId = operation.Id,
            Package = operation.Package,
            Target = operation.EffectiveTarget,
            Kind = operation.Kind,
            State = PackageOperationState.Completed
        };
    }
}
