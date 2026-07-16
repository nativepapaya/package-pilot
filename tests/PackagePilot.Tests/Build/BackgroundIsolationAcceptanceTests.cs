using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Services;
using PackagePilot.Windows.Services;

namespace PackagePilot.Tests.Build;

public sealed class BackgroundIsolationAcceptanceTests
{
    private static readonly string[] ForbiddenBackgroundSymbols =
    [
        "IWingetClient",
        "WingetClient",
        "Microsoft.Management.Deployment",
        "Windows.Management.Deployment",
        "PackageManager",
        "PackageCatalogReference",
        "IOperationQueue",
        "OperationQueue",
        "ISourceManagementService",
        "IPrivilegedSourceManagementBroker",
        "PrivilegedSourceRequest",
        "WindowsMsixPackageOperationClient",
        "ElevatedSourceManagementBroker",
        "ProcessStartInfo",
        "PackageOperation",
        "OperationResult",
        "IOperationDiagnosticsService",
        "WindowsOperationDiagnosticsService",
        "WindowsDeploymentEventLogReader",
        "OperationDiagnosticReference",
        "InstallPreferences",
        "PackageDetails",
        "InstallAsync",
        "UpgradeAsync",
        "UninstallAsync",
        "RemovePackageAsync",
        "RefreshSourceAsync",
        "AddSourceAsync",
        "RemoveSourceAsync",
        "ResetSourceAsync",
        "SetSourceExplicitAsync",
        "ExecuteElevatedAsync"
    ];

    private static readonly string[] ForbiddenReadOnlyInfrastructureSymbols =
    [
        "IWingetClient",
        "WingetClient",
        "IOperationQueue",
        "OperationQueue",
        "ISourceManagementService",
        "IPrivilegedSourceManagementBroker",
        "PrivilegedSourceRequest",
        "WindowsMsixPackageOperationClient",
        "ElevatedSourceManagementBroker",
        "ProcessStartInfo",
        "PackageOperation",
        "OperationResult",
        "IOperationDiagnosticsService",
        "WindowsOperationDiagnosticsService",
        "WindowsDeploymentEventLogReader",
        "OperationDiagnosticReference",
        "InstallPreferences",
        "PackageDetails",
        "InstallAsync",
        "UpgradeAsync",
        "UninstallAsync",
        "RemovePackageAsync",
        "RefreshSourceAsync",
        "AddSourceAsync",
        "RemoveSourceAsync",
        "ResetSourceAsync",
        "SetSourceExplicitAsync",
        "ExecuteElevatedAsync"
    ];

    [Fact]
    public void UpdateDiscoveryBoundaryContainsOnlyReadOnlyDiscovery()
    {
        Type boundary = typeof(IUpdateDiscoveryClient);
        var methods = boundary.GetMethods();

        var method = Assert.Single(methods);
        Assert.Equal(nameof(IUpdateDiscoveryClient.GetAvailableUpdatesAsync), method.Name);
        Assert.Equal(typeof(CancellationToken), Assert.Single(method.GetParameters()).ParameterType);

        Assert.Contains(boundary, typeof(IWingetClient).GetInterfaces());
        Assert.DoesNotContain(typeof(IWingetClient), typeof(WindowsUpdateDiscoveryClient).GetInterfaces());
        Assert.Equal(
            "PackagePilot.Windows.ReadOnly",
            typeof(WindowsUpdateDiscoveryClient).Assembly.GetName().Name);
        Assert.Same(
            typeof(WindowsUpdateDiscoveryClient).Assembly,
            typeof(WindowsBadgeService).Assembly);
        Assert.Same(
            typeof(WindowsUpdateDiscoveryClient).Assembly,
            typeof(WindowsIntegrationConstants).Assembly);
        Assert.DoesNotContain(
            typeof(WindowsUpdateDiscoveryClient).Assembly.GetReferencedAssemblies(),
            assembly => string.Equals(
                assembly.Name,
                "PackagePilot.Windows",
                StringComparison.Ordinal));
        var adapterMethod = Assert.Single(typeof(WindowsUpdateDiscoveryClient).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Equal(nameof(IUpdateDiscoveryClient.GetAvailableUpdatesAsync), adapterMethod.Name);

        var coordinatorConstructors = typeof(UpdateCoordinator).GetConstructors();
        Assert.Contains(
            coordinatorConstructors,
            constructor => constructor.GetParameters().FirstOrDefault()?.ParameterType == boundary);
        Assert.DoesNotContain(
            coordinatorConstructors,
            constructor => constructor.GetParameters().FirstOrDefault()?.ParameterType
                == typeof(IWingetClient));
    }

    [Fact]
    public void BackgroundProjectHasNoMutationOrElevationCompileSurface()
    {
        string repositoryRoot = FindRepositoryRoot();
        string backgroundDirectory = Path.Combine(repositoryRoot, "src", "PackagePilot.Background");

        foreach (string path in Directory.EnumerateFiles(
                     backgroundDirectory,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(path);
            foreach (string symbol in ForbiddenBackgroundSymbols)
            {
                Assert.DoesNotMatch(
                    new Regex($@"\b{Regex.Escape(symbol)}\b", RegexOptions.CultureInvariant),
                    source);
            }
        }

        string factory = File.ReadAllText(Path.Combine(
            backgroundDirectory,
            "BackgroundHostFactory.cs"));
        Assert.Contains("new WindowsUpdateDiscoveryClient()", factory, StringComparison.Ordinal);
        Assert.Contains("new UpdateCoordinator", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyInfrastructureHasNoMutationOrElevationCompileSurface()
    {
        string repositoryRoot = FindRepositoryRoot();
        string readOnlyDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.Windows.ReadOnly");

        foreach (string path in Directory.EnumerateFiles(
                     readOnlyDirectory,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(path);
            foreach (string symbol in ForbiddenReadOnlyInfrastructureSymbols)
            {
                Assert.DoesNotMatch(
                    new Regex($@"\b{Regex.Escape(symbol)}\b", RegexOptions.CultureInvariant),
                    source);
            }
        }

        string discoveryClient = File.ReadAllText(Path.Combine(
            readOnlyDirectory,
            "Services",
            "WindowsUpdateDiscoveryClient.cs"));
        Assert.Contains("new PackageManager()", discoveryClient, StringComparison.Ordinal);
        Assert.DoesNotContain("_inner", discoveryClient, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectReferencesEnforceThePhysicalReadOnlyBoundary()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument backgroundProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.Background",
            "PackagePilot.Background.csproj"));

        string[] backgroundReferences = backgroundProject
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(
                (string?)reference.Attribute("Include") ?? string.Empty))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["PackagePilot.Core", "PackagePilot.Windows.ReadOnly"],
            backgroundReferences);
        XElement backgroundComInteropReference = Assert.Single(
            backgroundProject.Descendants("PackageReference"),
            reference => string.Equals(
                (string?)reference.Attribute("Include"),
                "Microsoft.WindowsPackageManager.ComInterop",
                StringComparison.Ordinal));
        Assert.Equal("1.28.240", (string?)backgroundComInteropReference.Attribute("Version"));
        Assert.Equal("runtime", (string?)backgroundComInteropReference.Attribute("IncludeAssets"));
        Assert.Equal("all", (string?)backgroundComInteropReference.Attribute("PrivateAssets"));

        XElement dependencyGate = Assert.Single(
            backgroundProject.Descendants("Target"),
            target => string.Equals(
                (string?)target.Attribute("Name"),
                "ValidateBackgroundWinGetRuntimeDependency",
                StringComparison.Ordinal));
        Assert.Contains(
            dependencyGate.Descendants("Error"),
            error => ((string?)error.Attribute("Condition"))?.Contains(
                "Microsoft.Management.Deployment.CsWinRTProjection",
                StringComparison.Ordinal) == true);

        XElement compileGate = Assert.Single(
            backgroundProject.Descendants("Target"),
            target => string.Equals(
                (string?)target.Attribute("Name"),
                "ValidateBackgroundWinGetCompileIsolation",
                StringComparison.Ordinal));
        Assert.Contains(
            compileGate.Descendants("Error"),
            error => ((string?)error.Attribute("Text"))?.Contains(
                "outside the background host compile surface",
                StringComparison.Ordinal) == true);

        XDocument readOnlyProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.Windows.ReadOnly",
            "PackagePilot.Windows.ReadOnly.csproj"));
        var readOnlyReference = Assert.Single(readOnlyProject.Descendants("ProjectReference"));
        Assert.Equal(
            "PackagePilot.Core",
            Path.GetFileNameWithoutExtension(
                (string?)readOnlyReference.Attribute("Include") ?? string.Empty));

        XDocument windowsProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.Windows",
            "PackagePilot.Windows.csproj"));
        Assert.Contains(
            windowsProject.Descendants("ProjectReference"),
            reference => string.Equals(
                Path.GetFileNameWithoutExtension(
                    (string?)reference.Attribute("Include") ?? string.Empty),
                "PackagePilot.Windows.ReadOnly",
                StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startingPath in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(startingPath);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PackagePilot.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Package Pilot repository root.");
    }
}
