using System.Diagnostics;
using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;
using Xunit.Abstractions;
namespace PackagePilot.Tests.Integration;

[Collection(LiveWingetCollection.Name)]
public sealed class WingetClientLiveTests
{
    public const string OptInEnvironmentVariable = "PACKAGEPILOT_RUN_LIVE_WINGET_TESTS";
    private readonly ITestOutputHelper _output;

    public WingetClientLiveTests(ITestOutputHelper output) => _output = output;

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
    public async Task StartupReadModel_LoadsInventoriesAndSourcesConcurrently()
    {
        using var cancellation = CreateTimeout(TimeSpan.FromMinutes(2));
        var client = CreateClient();
        var elapsed = Stopwatch.StartNew();

        var capabilities = await client.GetCapabilitiesAsync(cancellation.Token);
        Assert.True(capabilities.MeetsMinimumContract, capabilities.UnavailableReason);

        var installedTask = client.GetInstalledPackagesAsync(cancellation.Token);
        var updatesTask = client.GetAvailableUpdatesAsync(cancellation.Token);
        var sourcesTask = client.GetSourcesAsync(cancellation.Token);
        await Task.WhenAll(installedTask, updatesTask, sourcesTask);

        Assert.NotNull(await installedTask);
        Assert.NotNull(await updatesTask);
        Assert.NotEmpty(await sourcesTask);
        _output.WriteLine($"Concurrent startup read model: {elapsed.Elapsed.TotalMilliseconds:F1} ms");
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
        var client = CreateClient();
        var elapsed = Stopwatch.StartNew();
        var updates = await client.GetAvailableUpdatesAsync(cancellation.Token);
        elapsed.Stop();

        Assert.NotNull(updates);
        Assert.All(updates, package =>
        {
            Assert.Equal(PackageStatus.UpdateAvailable, package.Status);
            Assert.False(string.IsNullOrWhiteSpace(package.InstalledVersion));
            Assert.False(string.IsNullOrWhiteSpace(package.AvailableVersion));
        });
        var sourceCount = (await client.GetSourcesAsync(cancellation.Token)).Count;
        _output.WriteLine(
            $"Update discovery: {elapsed.Elapsed.TotalMilliseconds:F1} ms "
            + $"({updates.Count} updates, {sourceCount} sources)");
    }

    [LiveWingetFact]
    public async Task CombinedUpdateDetection_MatchesPerSourceReference()
    {
        using var cancellation = CreateTimeout(TimeSpan.FromMinutes(3));
        var client = CreateClient();
        var sourceCount = (await client.GetSourcesAsync(cancellation.Token)).Count;

        var combinedElapsed = Stopwatch.StartNew();
        var combined = await client.GetAvailableUpdatesAsync(cancellation.Token);
        combinedElapsed.Stop();

        var perSourceElapsed = Stopwatch.StartNew();
        var perSource = await client.GetAvailableUpdatesPerSourceAsync(cancellation.Token);
        perSourceElapsed.Stop();

        Assert.Equal(CreateUpdateSnapshot(perSource), CreateUpdateSnapshot(combined));
        _output.WriteLine(
            $"Combined: {combinedElapsed.Elapsed.TotalMilliseconds:F1} ms; "
            + $"per-source: {perSourceElapsed.Elapsed.TotalMilliseconds:F1} ms "
            + $"({combined.Count} updates, {sourceCount} sources)");
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

    private static IReadOnlyList<UpdateSnapshot> CreateUpdateSnapshot(
        IEnumerable<PackageSummary> packages) => packages
        .Select(package => new UpdateSnapshot(
            package.Key,
            package.InstalledVersion,
            package.AvailableVersion))
        .OrderBy(package => package.Key.Id, StringComparer.OrdinalIgnoreCase)
        .ThenBy(package => package.Key.SourceId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(package => package.InstalledVersion, StringComparer.OrdinalIgnoreCase)
        .ThenBy(package => package.AvailableVersion, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private sealed record UpdateSnapshot(
        PackageKey Key,
        string? InstalledVersion,
        string? AvailableVersion);

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
