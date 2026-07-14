using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class JsonOperationHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "PackagePilot.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsResultsAndCapsHistoryAtOneHundred()
    {
        var path = Path.Combine(_directory, "history.json");
        var store = new JsonOperationHistoryStore(path);
        var source = Enumerable.Range(0, 105)
            .Select(index => Result(index, index == 0 ? PackageOperationState.Failed : PackageOperationState.Completed))
            .ToArray();

        await store.SaveAsync(source);
        var loaded = await store.LoadAsync();

        Assert.Equal(100, loaded.Count);
        Assert.Equal(source[0], loaded[0]);
        Assert.Equal(source[99], loaded[^1]);
        Assert.Equal(WingetErrorKind.Network, loaded[0].Error?.Kind);
    }

    [Fact]
    public async Task Load_ReturnsEmptyWhenFileDoesNotExist()
    {
        var store = new JsonOperationHistoryStore(Path.Combine(_directory, "missing.json"));

        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task Load_ReportsMalformedJsonAsInvalidData()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "history.json");
        await File.WriteAllTextAsync(path, "{ definitely not valid json }");
        var store = new JsonOperationHistoryStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static OperationResult Result(int index, PackageOperationState state) => new()
    {
        OperationId = Guid.CreateVersion7(),
        Package = new PackageKey($"Package.{index}", "winget"),
        Kind = PackageOperationKind.Install,
        State = state,
        StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(index),
        CompletedAt = DateTimeOffset.UnixEpoch.AddMinutes(index + 1),
        Error = state == PackageOperationState.Failed
            ? new WingetError
            {
                Kind = WingetErrorKind.Network,
                Code = "Network",
                Message = "No network."
            }
            : null
    };
}
