using PackagePilot.App.Services;
using PackagePilot.Core.Models;
namespace PackagePilot.Tests.Integration;

[Collection(LiveWingetCollection.Name)]
public sealed class WingetClientLiveTests
{
    public const string OptInEnvironmentVariable = "PACKAGEPILOT_RUN_LIVE_WINGET_TESTS";

    [LiveWingetFact]
    public async Task ComActivation_ReportsSupportedCapabilities()
    {
        using var cancellation = CreateTimeout();
        var capabilities = await CreateClient().GetCapabilitiesAsync(cancellation.Token);

        Assert.True(capabilities.IsAvailable, capabilities.UnavailableReason);
        Assert.True(capabilities.MeetsMinimumContract, capabilities.UnavailableReason);
        Assert.True(capabilities.ContractVersion >= WingetCapabilities.RequiredContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(capabilities.Version));
    }

    [LiveWingetFact]
    public async Task ConfiguredSources_CanBeEnumeratedWithoutMutation()
    {
        using var cancellation = CreateTimeout();
        var sources = await CreateClient().GetSourcesAsync(cancellation.Token);

        Assert.NotEmpty(sources);
        Assert.All(sources, source =>
        {
            Assert.False(string.IsNullOrWhiteSpace(source.Id));
            Assert.False(string.IsNullOrWhiteSpace(source.Name));
        });
        Assert.Contains(sources, source => source.Health == SourceHealth.Healthy);
    }

    [LiveWingetFact]
    public async Task CatalogSearch_ReturnsMappedPackagesAndSourceHealth()
    {
        using var cancellation = CreateTimeout();
        var result = await SearchKnownPackageAsync(CreateClient(), cancellation.Token);

        Assert.NotEmpty(result.Sources);
        Assert.NotEmpty(result.Packages);
        Assert.All(result.Packages, package =>
        {
            Assert.False(package.Key.IsEmpty);
            Assert.False(string.IsNullOrWhiteSpace(package.Name));
            Assert.False(string.IsNullOrWhiteSpace(package.SourceName));
        });
    }

    [LiveWingetFact]
    public async Task InstalledInventory_CanBeReadAndMapped()
    {
        using var cancellation = CreateTimeout();
        var packages = await CreateClient().GetInstalledPackagesAsync(cancellation.Token);

        Assert.NotNull(packages);
        Assert.All(packages, package =>
        {
            Assert.False(package.Key.IsEmpty);
            Assert.False(string.IsNullOrWhiteSpace(package.Name));
            Assert.True(package.Status is PackageStatus.Installed
                or PackageStatus.UpdateAvailable
                or PackageStatus.Unmatched);
        });
    }

    [LiveWingetFact]
    public async Task PackageMetadata_CanBeReadForKnownCatalogPackage()
    {
        using var cancellation = CreateTimeout();
        var client = CreateClient();
        var search = await SearchKnownPackageAsync(client, cancellation.Token);
        var package = Assert.Single(search.Packages.Take(1));

        var details = await client.GetPackageDetailsAsync(
            package.Key,
            cancellationToken: cancellation.Token);

        Assert.NotNull(details);
        Assert.Equal(package.Key, details.Summary.Key);
        Assert.False(string.IsNullOrWhiteSpace(details.Summary.Name));
        Assert.NotEmpty(details.Versions);
    }

    [LiveWingetFact]
    public async Task UpdateDetection_IsReadOnlyAndReturnsOnlyUpdates()
    {
        using var cancellation = CreateTimeout(TimeSpan.FromMinutes(2));
        var updates = await CreateClient().GetAvailableUpdatesAsync(cancellation.Token);

        Assert.NotNull(updates);
        Assert.All(updates, package =>
        {
            Assert.Equal(PackageStatus.UpdateAvailable, package.Status);
            Assert.False(string.IsNullOrWhiteSpace(package.InstalledVersion));
            Assert.False(string.IsNullOrWhiteSpace(package.AvailableVersion));
        });
    }

    private static WingetClient CreateClient() => new();

    private static async Task<PackageSearchResult> SearchKnownPackageAsync(
        WingetClient client,
        CancellationToken cancellationToken)
    {
        var sources = await client.GetSourcesAsync(cancellationToken);
        var source = sources.FirstOrDefault(item =>
            item.Health == SourceHealth.Healthy
            && item.Name.Contains("winget", StringComparison.OrdinalIgnoreCase)
            && !item.Name.Contains("font", StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            Assert.Fail(
                "The read-only live test requires an already-consented, healthy WinGet community source.");
        }

        return await client.SearchAsync(new PackageQuery
        {
            SearchText = "Microsoft.PowerToys",
            SourceId = source.Id,
            MatchField = PackageMatchField.Id,
            Limit = 5
        }, cancellationToken);
    }

    private static CancellationTokenSource CreateTimeout(TimeSpan? timeout = null) =>
        new(timeout ?? TimeSpan.FromSeconds(60));

}

[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveWingetFactAttribute : FactAttribute
{
    public LiveWingetFactAttribute()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable(WingetClientLiveTests.OptInEnvironmentVariable),
            "1",
            StringComparison.Ordinal))
        {
            Skip = $"Set {WingetClientLiveTests.OptInEnvironmentVariable}=1 to run read-only tests against the local WinGet installation.";
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveWingetCollection
{
    public const string Name = "Live WinGet (read-only)";
}
