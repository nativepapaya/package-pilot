using PackagePilot.Core.Models;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Windows;

public sealed class WindowsAppIconResourceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PackagePilot.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DisplayIcon_ParsesQuotedExecutableAndResourceIndex()
    {
        var path = CreateFile("Product, Stable.exe", [1, 2, 3]);

        var parsed = WindowsDisplayIconReferenceParser.TryCreate(
            $"\"{path}\",-42",
            out var reference);

        Assert.True(parsed);
        Assert.NotNull(reference);
        Assert.Equal(AppIconSourceKind.ValidatedExecutableResource, reference.Kind);
        Assert.Equal(Path.GetFullPath(path), reference.ResourcePath);
        Assert.Equal(-42, reference.ResourceIndex);
    }

    [Fact]
    public void DisplayIcon_AcceptsBoundedRasterResource()
    {
        var path = CreateFile("Product.ico", [1, 2, 3]);

        var parsed = WindowsDisplayIconReferenceParser.TryCreate(path, out var reference);

        Assert.True(parsed);
        Assert.NotNull(reference);
        Assert.Equal(AppIconSourceKind.ValidatedLocalResource, reference.Kind);
        Assert.Equal(Path.GetFullPath(path), reference.ResourcePath);
    }

    [Theory]
    [InlineData(@"\\server\share\Product.ico")]
    [InlineData(@"shell:AppsFolder\Product")]
    [InlineData(@"::{00000000-0000-0000-0000-000000000000}")]
    [InlineData("https://example.test/Product.png")]
    [InlineData(@"relative\Product.ico")]
    public void DisplayIcon_RejectsNonLocalOrShellReferences(string value)
    {
        Assert.False(WindowsDisplayIconReferenceParser.TryCreate(value, out var reference));
        Assert.Null(reference);
    }

    [Fact]
    public void PackageAsset_ChoosesAQualifiedTargetSizeAsset()
    {
        var packageRoot = Path.Combine(_root, "Package");
        var assets = Path.Combine(packageRoot, "Assets");
        Directory.CreateDirectory(assets);
        var scaled = Path.Combine(assets, "StoreLogo.scale-200.png");
        var targetSized = Path.Combine(assets, "StoreLogo.targetsize-64.png");
        File.WriteAllBytes(scaled, [1]);
        File.WriteAllBytes(targetSized, [2]);

        var resolved = WindowsPackageAssetResolver.TryResolve(
            packageRoot,
            @"Assets\StoreLogo.png",
            out var reference);

        Assert.True(resolved);
        Assert.NotNull(reference);
        Assert.Equal(AppIconSourceKind.MsixPackageAsset, reference.Kind);
        Assert.Equal(targetSized, reference.ResourcePath, ignoreCase: true);
    }

    [Fact]
    public void PackageAsset_RejectsTraversalOutsideThePackage()
    {
        var packageRoot = Path.Combine(_root, "Package");
        Directory.CreateDirectory(packageRoot);
        CreateFile("Outside.png", [1]);

        Assert.False(WindowsPackageAssetResolver.TryResolve(
            packageRoot,
            @"..\Outside.png",
            out var reference));
        Assert.Null(reference);
    }

    [Fact]
    public async Task ExecutableIconExtraction_ReturnsABoundedPngWithoutLaunchingTheFile()
    {
        var shell32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "shell32.dll");
        Assert.True(WindowsDisplayIconReferenceParser.TryCreate(
            $"\"{shell32}\",0",
            out var reference));

        var bytes = await WindowsExecutableIconExtractor.Shared.GetIconPngAsync(reference, 64);

        Assert.NotNull(bytes);
        Assert.InRange(bytes.Length, 9, 1024 * 1024);
        Assert.Equal(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            bytes.Take(8));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for a diagnostic-only temporary directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for a diagnostic-only temporary directory.
        }
    }

    private string CreateFile(string relativePath, byte[] contents)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, relativePath);
        File.WriteAllBytes(path, contents);
        return path;
    }
}
