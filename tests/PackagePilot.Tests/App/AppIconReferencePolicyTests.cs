using PackagePilot.App.Services;
using PackagePilot.Core.Models;

namespace PackagePilot.Tests.App;

public sealed class AppIconReferencePolicyTests
{
    [Theory]
    [InlineData(@"\\server\share\icon.png")]
    [InlineData(@"\\?\C:\icons\icon.png")]
    [InlineData(@"\\.\C:\icons\icon.png")]
    [InlineData("https://example.test/icon.png")]
    [InlineData("shell:AppsFolder")]
    [InlineData("::{4234d49b-0245-4df3-b780-3893943456e1}")]
    [InlineData(@"C:\icons\icon.exe")]
    [InlineData("relative-icon.png")]
    public void DangerousOrUnsupportedLocalReferences_AreRejected(string path)
    {
        Assert.False(AppIconReferencePolicy.TryCreateValidatedLocal(
            path,
            1024,
            null,
            out var reference));
        Assert.Null(reference);
    }

    [Fact]
    public void OversizedLocalImage_IsRejected()
    {
        Assert.False(AppIconReferencePolicy.TryCreateValidatedLocal(
            @"C:\icons\icon.png",
            AppIconReferencePolicy.MaxLocalIconBytes + 1,
            null,
            out _));
    }

    [Fact]
    public void BoundedAbsoluteRasterPath_CreatesNonExecutableReference()
    {
        Assert.True(AppIconReferencePolicy.TryCreateValidatedLocal(
            @"C:\icons\icon.png",
            2048,
            0,
            out var reference));
        Assert.Equal(AppIconSourceKind.ValidatedLocalResource, reference!.Kind);
        Assert.Equal(@"C:\icons\icon.png", reference.ResourcePath);
    }
}
