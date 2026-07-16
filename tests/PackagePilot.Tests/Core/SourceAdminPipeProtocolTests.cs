using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Core;

public sealed class SourceAdminPipeProtocolTests
{
    [Fact]
    public async Task AuthenticatedPipe_ExchangesExactlyOneTypedRequestAndResponse()
    {
        var pipeName = SourceAdminPipeProtocol.CreatePipeName();
        var secret = SourceAdminPipeProtocol.CreateSecret();
        var request = PrivilegedSourceRequest.Remove("contoso");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = ElevatedPipeAclFactory.CreateServerForCurrentUser(pipeName);

        var clientTask = Task.Run(async () =>
        {
            using var client = ElevatedPipeAclFactory.CreateClient(pipeName);
            await client.ConnectAsync(timeout.Token);
            await SourceAdminPipeProtocol.AuthenticateClientAsync(
                client,
                pipeName,
                secret,
                timeout.Token);
            var received = await SourceAdminPipeProtocol.ReadJsonAsync<PrivilegedSourceRequest>(
                client,
                timeout.Token);
            await SourceAdminPipeProtocol.WriteJsonAsync(
                client,
                new PrivilegedSourceResponse
                {
                    RequestId = received.RequestId,
                    Result = new SourceOperationResult
                    {
                        OperationId = Guid.NewGuid(),
                        Kind = received.Kind,
                        SourceName = received.SourceName,
                        Status = SourceOperationStatus.Succeeded,
                        Message = "Removed"
                    }
                },
                timeout.Token);
        }, timeout.Token);

        await server.WaitForConnectionAsync(timeout.Token);
        await SourceAdminPipeProtocol.AuthenticateServerAsync(
            server,
            pipeName,
            secret,
            timeout.Token);
        await SourceAdminPipeProtocol.WriteJsonAsync(server, request, timeout.Token);
        var response = await SourceAdminPipeProtocol.ReadJsonAsync<PrivilegedSourceResponse>(
            server,
            timeout.Token);
        await clientTask;

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(SourceOperationStatus.Succeeded, response.Result.Status);
        Assert.Equal("contoso", response.Result.SourceName);
    }

    [Fact]
    public async Task Authentication_RejectsWrongOneShotSecret()
    {
        var pipeName = SourceAdminPipeProtocol.CreatePipeName();
        var serverSecret = SourceAdminPipeProtocol.CreateSecret();
        var clientSecret = SourceAdminPipeProtocol.CreateSecret();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = ElevatedPipeAclFactory.CreateServerForCurrentUser(pipeName);

        var clientTask = Task.Run(async () =>
        {
            using var client = ElevatedPipeAclFactory.CreateClient(pipeName);
            await client.ConnectAsync(timeout.Token);
            await SourceAdminPipeProtocol.AuthenticateClientAsync(
                client,
                pipeName,
                clientSecret,
                timeout.Token);
        }, timeout.Token);

        await server.WaitForConnectionAsync(timeout.Token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            SourceAdminPipeProtocol.AuthenticateServerAsync(
                server,
                pipeName,
                serverSecret,
                timeout.Token));
        await clientTask;
    }

    [Fact]
    public async Task StrictJson_RejectsCustomHeadersAndOtherUnknownFields()
    {
        var request = PrivilegedSourceRequest.Add(new AddPackageSourceRequest
        {
            Name = "contoso",
            Location = "https://packages.contoso.test/index",
            Type = PackageSourceType.Rest
        });
        var json = JsonSerializer.Serialize(
            request,
            SourceAdminPipeProtocol.CreateSerializerOptions());
        json = json[..^1] + ",\"customHeaders\":{\"authorization\":\"secret\"}}";
        using var stream = CreateFramedStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<JsonException>(() =>
            SourceAdminPipeProtocol.ReadJsonAsync<PrivilegedSourceRequest>(stream));
    }

    [Fact]
    public async Task Reader_RejectsOversizedFrameBeforeAllocation()
    {
        using var stream = new MemoryStream();
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            header,
            SourceAdminPipeProtocol.MaximumFrameBytes + 1);
        await stream.WriteAsync(header);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SourceAdminPipeProtocol.ReadJsonAsync<PrivilegedSourceRequest>(stream));
    }

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
}
