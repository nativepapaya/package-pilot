using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.App;

public sealed class WingetClientUpdateQueryTests
{
    [Fact]
    public async Task CombinedSuccess_SkipsPerSourceFallback()
    {
        var combinedCalls = 0;
        var singleCalls = 0;

        var results = await WindowsUpdateDiscoveryClient.QueryCombinedThenFallbackAsync<string, int>(
            ["one", "two"],
            (_, _) =>
            {
                combinedCalls++;
                return Success(42);
            },
            (_, _) =>
            {
                singleCalls++;
                return Success(0);
            });

        Assert.Equal([42], results);
        Assert.Equal(1, combinedCalls);
        Assert.Equal(0, singleCalls);
    }

    [Fact]
    public async Task CombinedSuccessWithNoUpdates_SkipsPerSourceFallback()
    {
        var singleCalls = 0;

        var results = await WindowsUpdateDiscoveryClient.QueryCombinedThenFallbackAsync<string, int>(
            ["one", "two"],
            (_, _) => Success<int>(),
            (_, _) =>
            {
                singleCalls++;
                return Success(1);
            });

        Assert.Empty(results);
        Assert.Equal(0, singleCalls);
    }

    [Fact]
    public async Task CombinedFailure_PropagatesAnyPerSourceFailure()
    {
        var attemptedSources = new List<string>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsUpdateDiscoveryClient.QueryCombinedThenFallbackAsync<string, string>(
                ["one", "broken", "three"],
                (_, _) => Failure<string>(),
                (source, _) =>
                {
                    attemptedSources.Add(source);
                    return source == "broken" ? Failure<string>() : Success(source);
                }));

        Assert.Contains("source query", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["one", "broken"], attemptedSources);
    }

    [Fact]
    public async Task PerSourceException_IsNotConvertedToAZeroUpdateSuccess()
    {
        var expected = new IOException("private source is offline");

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            WindowsUpdateDiscoveryClient.QueryCombinedThenFallbackAsync<string, int>(
                ["winget", "private"],
                (_, _) => Failure<int>(),
                (source, _) => source == "private"
                    ? Task.FromException<IReadOnlyList<int>?>(expected)
                    : Success<int>()));

        Assert.Same(expected, exception);
    }

    [Fact]
    public async Task CancellationAfterCombinedFailure_DoesNotStartFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var singleCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsUpdateDiscoveryClient.QueryCombinedThenFallbackAsync<string, int>(
                ["one", "two"],
                (_, _) =>
                {
                    cancellation.Cancel();
                    return Failure<int>();
                },
                (_, _) =>
                {
                    singleCalls++;
                    return Success(1);
                },
                cancellation.Token));

        Assert.Equal(0, singleCalls);
    }

    [Fact]
    public async Task CancellationBeforeCombinedSuccessReturns_IsObserved()
    {
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsUpdateDiscoveryClient.QueryCombinedThenFallbackAsync<string, int>(
                ["one", "two"],
                (_, _) =>
                {
                    cancellation.Cancel();
                    return Success(42);
                },
                (_, _) => Success(0),
                cancellation.Token));
    }

    [Fact]
    public async Task CancellationBeforeFinalSingleSourceReturns_IsObserved()
    {
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsUpdateDiscoveryClient.QueryPerSourceAsync<string, int>(
                ["one"],
                (_, _) =>
                {
                    cancellation.Cancel();
                    return Success(42);
                },
                cancellation.Token));
    }

    [Fact]
    public async Task SingleSource_SkipsCombinedAttempt()
    {
        var combinedCalls = 0;

        var results = await WindowsUpdateDiscoveryClient.QueryCombinedThenFallbackAsync<string, string>(
            ["one"],
            (_, _) =>
            {
                combinedCalls++;
                return Success("combined");
            },
            (source, _) => Success(source));

        Assert.Equal(["one"], results);
        Assert.Equal(0, combinedCalls);
    }

    [Fact]
    public void UpdatesHaveKnownSources_RejectsEmptyOrUnknownSourceIds()
    {
        var known = new PackageSummary { Key = new PackageKey("Package.One", "winget") };
        var empty = new PackageSummary { Key = new PackageKey("Package.Two", string.Empty) };
        var unknown = new PackageSummary { Key = new PackageKey("Package.Three", "private") };

        Assert.True(WindowsUpdateDiscoveryClient.UpdatesHaveKnownSources([known], ["winget", "msstore"]));
        Assert.True(WindowsUpdateDiscoveryClient.UpdatesHaveKnownSources([], ["winget", "msstore"]));
        Assert.False(WindowsUpdateDiscoveryClient.UpdatesHaveKnownSources([empty], ["winget", "msstore"]));
        Assert.False(WindowsUpdateDiscoveryClient.UpdatesHaveKnownSources([unknown], ["winget", "msstore"]));
    }

    [Fact]
    public void HasSingleAttributedSource_RejectsMissingAndMultipleSources()
    {
        Assert.True(WindowsUpdateDiscoveryClient.HasSingleAttributedSource(
            [("winget", "winget"), ("winget", "winget")]));
        Assert.True(WindowsUpdateDiscoveryClient.HasSingleAttributedSource(
            [(null, "Private Feed"), (null, "Private Feed")]));
        Assert.False(WindowsUpdateDiscoveryClient.HasSingleAttributedSource([]));
        Assert.False(WindowsUpdateDiscoveryClient.HasSingleAttributedSource([(null, null)]));
        Assert.False(WindowsUpdateDiscoveryClient.HasSingleAttributedSource(
            [("private", "Private Feed"), ("winget", "winget")]));
    }

    [Fact]
    public void SourceAliasesOverlap_MatchesRestSourceNameWhenConnectedIdChanges()
    {
        Assert.True(WindowsUpdateDiscoveryClient.SourceAliasesOverlap(
            "server-assigned-id",
            "Private Feed",
            "Private Feed",
            "Private Feed"));
        Assert.False(WindowsUpdateDiscoveryClient.SourceAliasesOverlap(
            "server-assigned-id",
            "Private Feed",
            "winget",
            "winget"));
    }

    [Fact]
    public async Task CombinedPackageFromMultipleSources_PreservesSourceSpecificFallbackRows()
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["private"] = "3.1.0-private",
            ["winget"] = "3.0.0"
        };

        var results = await WindowsUpdateDiscoveryClient.QueryCombinedThenFallbackAsync<string, PackageSummary>(
            ["private", "winget"],
            (_, _) => WindowsUpdateDiscoveryClient.HasSingleAttributedSource(
                [("private", "Private Feed"), ("winget", "winget")])
                    ? Success(new PackageSummary
                    {
                        Key = new PackageKey("Contoso.Tool", "private"),
                        SourceName = "Private Feed",
                        AvailableVersion = versions["private"]
                    })
                    : Failure<PackageSummary>(),
            (source, _) => Success(new PackageSummary
            {
                Key = new PackageKey("Contoso.Tool", source),
                SourceName = source,
                AvailableVersion = versions[source]
            }));

        Assert.Collection(
            results,
            package =>
            {
                Assert.Equal(new PackageKey("Contoso.Tool", "private"), package.Key);
                Assert.Equal("3.1.0-private", package.AvailableVersion);
            },
            package =>
            {
                Assert.Equal(new PackageKey("Contoso.Tool", "winget"), package.Key);
                Assert.Equal("3.0.0", package.AvailableVersion);
            });
    }

    private static Task<IReadOnlyList<T>?> Success<T>(params T[] values) =>
        Task.FromResult<IReadOnlyList<T>?>(values);

    private static Task<IReadOnlyList<T>?> Failure<T>() =>
        Task.FromResult<IReadOnlyList<T>?>(null);
}
