using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class JsonBackgroundUpdateRunStatusStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"PackagePilot.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task RoundTripsVersionedStatus()
    {
        var path = Path.Combine(_directory, "background-update-status.json");
        var store = new JsonBackgroundUpdateRunStatusStore(path);
        var status = new BackgroundUpdateRunStatus
        {
            State = BackgroundUpdateRunState.Failed,
            AttemptedAt = DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
            CompletedAt = DateTimeOffset.Parse("2026-07-14T12:00:03Z"),
            ForegroundFallbackRequired = true,
            Message = "WinGet unavailable"
        };

        await store.SaveAsync(status);
        var loaded = await store.LoadAsync();

        Assert.Equal(status, loaded);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task InvalidJsonIsIgnored()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "background-update-status.json");
        await File.WriteAllTextAsync(path, "{ invalid");

        var loaded = await new JsonBackgroundUpdateRunStatusStore(path).LoadAsync();

        Assert.Null(loaded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
