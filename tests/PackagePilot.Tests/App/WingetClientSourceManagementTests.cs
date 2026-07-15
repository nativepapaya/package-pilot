using Microsoft.Management.Deployment;
using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.App;

public sealed class WingetClientSourceManagementTests
{
    [Fact]
    public void CapabilityProjection_PreservesSupportAndCurrentProcessAccess()
    {
        var capabilities = WingetClient.CreateSourceManagementCapabilities(
            new WingetCapabilities
            {
                IsAvailable = true,
                ContractVersion = 28
            },
            isCurrentProcessElevated: true);

        Assert.True(capabilities.SupportsRefresh);
        Assert.True(capabilities.SupportsExplicitEdit);
        Assert.True(capabilities.CanMutateInCurrentProcess);
        Assert.False(capabilities.MutationsRequireElevation);
    }

    [Theory]
    [InlineData(RefreshPackageCatalogStatus.Ok, SourceOperationStatus.Succeeded)]
    [InlineData(RefreshPackageCatalogStatus.GroupPolicyError, SourceOperationStatus.BlockedByPolicy)]
    [InlineData(RefreshPackageCatalogStatus.CatalogError, SourceOperationStatus.Unavailable)]
    [InlineData(RefreshPackageCatalogStatus.InternalError, SourceOperationStatus.Failed)]
    public void RefreshStatus_IsMappedWithoutLosingPolicyFailures(
        RefreshPackageCatalogStatus native,
        SourceOperationStatus expected)
    {
        Assert.Equal(expected, WingetClient.MapSourceStatus(native));
    }

    [Theory]
    [InlineData(AddPackageCatalogStatus.Ok, SourceOperationStatus.Succeeded)]
    [InlineData(AddPackageCatalogStatus.GroupPolicyError, SourceOperationStatus.BlockedByPolicy)]
    [InlineData(AddPackageCatalogStatus.CatalogError, SourceOperationStatus.Unavailable)]
    [InlineData(AddPackageCatalogStatus.InvalidOptions, SourceOperationStatus.InvalidRequest)]
    [InlineData(AddPackageCatalogStatus.AccessDenied, SourceOperationStatus.AccessDenied)]
    [InlineData(AddPackageCatalogStatus.AuthenticationError, SourceOperationStatus.AuthenticationRequired)]
    [InlineData(AddPackageCatalogStatus.InternalError, SourceOperationStatus.Failed)]
    public void AddStatus_IsMappedForUiAndFutureElevationBroker(
        AddPackageCatalogStatus native,
        SourceOperationStatus expected)
    {
        Assert.Equal(expected, WingetClient.MapSourceStatus(native));
    }

    [Theory]
    [InlineData(RemovePackageCatalogStatus.Ok, SourceOperationStatus.Succeeded)]
    [InlineData(RemovePackageCatalogStatus.GroupPolicyError, SourceOperationStatus.BlockedByPolicy)]
    [InlineData(RemovePackageCatalogStatus.CatalogError, SourceOperationStatus.Unavailable)]
    [InlineData(RemovePackageCatalogStatus.AccessDenied, SourceOperationStatus.AccessDenied)]
    [InlineData(RemovePackageCatalogStatus.InvalidOptions, SourceOperationStatus.InvalidRequest)]
    [InlineData(RemovePackageCatalogStatus.InternalError, SourceOperationStatus.Failed)]
    public void RemoveAndResetStatus_IsMappedForSafeNamedOperations(
        RemovePackageCatalogStatus native,
        SourceOperationStatus expected)
    {
        Assert.Equal(expected, WingetClient.MapSourceStatus(native));
    }

    [Theory]
    [InlineData(EditPackageCatalogStatus.Ok, SourceOperationStatus.Succeeded)]
    [InlineData(EditPackageCatalogStatus.GroupPolicyError, SourceOperationStatus.BlockedByPolicy)]
    [InlineData(EditPackageCatalogStatus.CatalogError, SourceOperationStatus.Unavailable)]
    [InlineData(EditPackageCatalogStatus.AccessDenied, SourceOperationStatus.AccessDenied)]
    [InlineData(EditPackageCatalogStatus.InvalidOptions, SourceOperationStatus.InvalidRequest)]
    [InlineData(EditPackageCatalogStatus.InternalError, SourceOperationStatus.Failed)]
    public void EditStatus_IsMappedForExplicitOnlyEdits(
        EditPackageCatalogStatus native,
        SourceOperationStatus expected)
    {
        Assert.Equal(expected, WingetClient.MapSourceStatus(native));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(100, 100)]
    [InlineData(125, 100)]
    public void SourceProgress_UsesNativeZeroToOneHundredScale(double native, double expected)
    {
        Assert.Equal(expected, WingetClient.NormalizeSourcePercent(native));
    }

    [Fact]
    public void InvalidSourceProgress_IsNotPublishedAsAPercentage()
    {
        Assert.Null(WingetClient.NormalizeSourcePercent(-1));
        Assert.Null(WingetClient.NormalizeSourcePercent(double.NaN));
        Assert.Null(WingetClient.NormalizeSourcePercent(double.PositiveInfinity));
    }
}
