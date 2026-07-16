using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Windows;

public sealed class WindowsAppxDeploymentEventLogReaderTests
{
    [Fact]
    public void Query_UsesOnlyTheExactUppercaseActivityIdentifier()
    {
        var activityId = Guid.Parse("7ad6ca90-d2db-4b97-8f53-60d85ad18ef1");

        var query = AppxDeploymentEventLogQuery.Create(activityId);

        Assert.Equal(
            "*[System/Correlation[@ActivityID='{7AD6CA90-D2DB-4B97-8F53-60D85AD18EF1}']]",
            query);
        Assert.DoesNotContain("TimeCreated", query, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Query_RejectsAnEmptyActivityIdentifier()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => AppxDeploymentEventLogQuery.Create(Guid.Empty));

        Assert.Equal("activityId", exception.ParamName);
    }

    [Fact]
    public void Limits_AreHardCodedToBoundNativeReads()
    {
        Assert.Equal(200, WindowsDeploymentEventLogReader.MaximumEventCount);
        Assert.Equal(1024 * 1024, WindowsDeploymentEventLogReader.MaximumRenderedBytes);
    }
}
