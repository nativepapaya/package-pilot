using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.Tests.Core;

public sealed class ActivationRouterTests
{
    private readonly ActivationRouter _router = new();

    [Theory]
    [InlineData("packagepilot://installed", AppDestination.Installed)]
    [InlineData("packagepilot://updates", AppDestination.Updates)]
    [InlineData("packagepilot://activity", AppDestination.Activity)]
    [InlineData("packagepilot://settings", AppDestination.Settings)]
    [InlineData("packagepilot://sources", AppDestination.Sources)]
    [InlineData("packagepilot:discover", AppDestination.Discover)]
    public void Protocol_AllowsKnownDestinations(string value, AppDestination expected)
    {
        var result = _router.ParseProtocol(new Uri(value));

        Assert.True(result.IsAccepted);
        Assert.Equal(expected, result.Request!.Destination);
    }

    [Fact]
    public void Protocol_DecodesDiscoverQuery()
    {
        var result = _router.ParseProtocol(new Uri("packagepilot://discover?query=PowerToys%20Run"));

        Assert.True(result.IsAccepted);
        Assert.Equal("PowerToys Run", result.Request!.SearchQuery);
    }

    [Theory]
    [InlineData("https://updates")]
    [InlineData("packagepilot://install?id=Contoso.App")]
    [InlineData("packagepilot://updates?package=Contoso.App")]
    [InlineData("packagepilot://discover/path")]
    [InlineData("packagepilot://user@updates")]
    [InlineData("packagepilot://updates#fragment")]
    public void Protocol_RejectsMalformedOrDestructiveRoutes(string value)
    {
        var result = _router.ParseProtocol(new Uri(value));

        Assert.False(result.IsAccepted);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("discover", AppDestination.Discover)]
    [InlineData("updates", AppDestination.Updates)]
    [InlineData("installed", AppDestination.Installed)]
    [InlineData("sources", AppDestination.Sources)]
    public void CommandLine_AllowsReadOnlyDestinations(string command, AppDestination expected)
    {
        var result = _router.ParseCommandLine([command]);

        Assert.True(result.IsAccepted);
        Assert.Equal(expected, result.Request!.Destination);
    }

    [Fact]
    public void CommandLine_SearchPreservesMultiWordQuery()
    {
        var result = _router.ParseCommandLine(["search", "Visual", "Studio", "Code"]);

        Assert.True(result.IsAccepted);
        Assert.Equal(AppDestination.Discover, result.Request!.Destination);
        Assert.Equal("Visual Studio Code", result.Request.SearchQuery);
    }

    [Fact]
    public void CommandLine_CheckIsReadOnlyAndExplicit()
    {
        var result = _router.ParseCommandLine(["check"]);

        Assert.True(result.IsAccepted);
        Assert.Equal(AppDestination.Updates, result.Request!.Destination);
        Assert.True(result.Request.CheckForUpdates);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("uninstall")]
    [InlineData("add-source")]
    [InlineData("remove-source")]
    public void CommandLine_RejectsMutationCommands(string command)
    {
        Assert.False(_router.ParseCommandLine([command, "anything"]).IsAccepted);
    }

    [Fact]
    public void Protocol_RejectsOversizedSearch()
    {
        var uri = new Uri($"packagepilot://discover?query={new string('a', ActivationRouter.MaximumSearchLength + 1)}");

        Assert.False(_router.ParseProtocol(uri).IsAccepted);
    }
}
