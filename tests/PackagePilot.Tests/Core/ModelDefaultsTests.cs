using PackagePilot.Core.Models;

namespace PackagePilot.Tests.Core;

public sealed class ModelDefaultsTests
{
    [Fact]
    public void PackageQuery_ClampsLimitToV1Maximum()
    {
        Assert.Equal(100, new PackageQuery().Limit);
        Assert.Equal(100, new PackageQuery { Limit = 500 }.Limit);
        Assert.Equal(1, new PackageQuery { Limit = 0 }.Limit);
    }

    [Fact]
    public void PackageQuery_NeverAcceptsSourceAgreementsByDefault()
    {
        Assert.False(new PackageQuery().AcceptSourceAgreements);
        Assert.True(new PackageQuery { AcceptSourceAgreements = true }.AcceptSourceAgreements);
    }

    [Fact]
    public void InstallPreferences_NeverAcceptsAgreementsByDefault()
    {
        var preferences = new InstallPreferences();

        Assert.False(preferences.AcceptSourceAgreements);
        Assert.False(preferences.AcceptPackageAgreements);
        Assert.True(preferences.AllowElevation);
        Assert.Equal(InstallerScope.Unknown, preferences.Scope);
    }

    [Fact]
    public void PackageDetails_CollectionsHaveSafeEmptyDefaults()
    {
        var details = new PackageDetails();

        Assert.Empty(details.Tags);
        Assert.Empty(details.Versions);
        Assert.Empty(details.Agreements);
        Assert.NotNull(details.Summary);
    }

    [Fact]
    public void PackageSourceStatus_AgreementsHaveSafeEmptyDefault()
    {
        Assert.Empty(new PackageSourceStatus().Agreements);
    }

    [Theory]
    [InlineData(false, 6u, false)]
    [InlineData(true, 5u, false)]
    [InlineData(true, 6u, true)]
    [InlineData(true, 7u, true)]
    public void WingetCapabilities_GatesContractSixFeatures(
        bool available,
        uint contractVersion,
        bool expected)
    {
        var capabilities = new WingetCapabilities
        {
            IsAvailable = available,
            ContractVersion = contractVersion
        };

        Assert.Equal(expected, capabilities.MeetsMinimumContract);
        Assert.Equal(expected, capabilities.SupportsPackageMetadata);
        Assert.Equal(expected, capabilities.SupportsAgreementHandling);
    }

    [Theory]
    [InlineData(PackageOperationState.Queued, true)]
    [InlineData(PackageOperationState.Resolving, true)]
    [InlineData(PackageOperationState.Downloading, true)]
    [InlineData(PackageOperationState.Installing, false)]
    [InlineData(PackageOperationState.Completed, false)]
    public void OperationProgress_ExposesCancellationBoundary(
        PackageOperationState state,
        bool expected)
    {
        Assert.Equal(expected, new OperationProgress { State = state }.CanCancel);
    }

    [Fact]
    public void PackageOperationCreate_UsesPackageIdAsFallbackDisplayName()
    {
        var operation = PackageOperation.Create(
            PackageOperationKind.Install,
            new PackageKey("Microsoft.PowerToys", "winget"));

        Assert.NotEqual(Guid.Empty, operation.Id);
        Assert.Equal("Microsoft.PowerToys", operation.DisplayName);
        Assert.Equal(PackageOperationKind.Install, operation.Kind);
    }
}
