using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.App;

public sealed class WindowsUpdateDiscoveryClientTests
{
    [Fact]
    public async Task GetAvailableUpdatesAsync_DelegatesThroughNarrowBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new PackageSummary
        {
            Key = new PackageKey("Contoso.Tool", "winget"),
            Name = "Contoso Tool",
            AvailableVersion = "2.0",
            Status = PackageStatus.UpdateAvailable
        };
        CancellationToken? observedCancellationToken = null;
        var client = new WindowsUpdateDiscoveryClient(cancellationToken =>
        {
            observedCancellationToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<PackageSummary>>([expected]);
        });

        var updates = await client.GetAvailableUpdatesAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, observedCancellationToken);
        Assert.Same(expected, Assert.Single(updates));
    }
}
