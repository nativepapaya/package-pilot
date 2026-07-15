using System.Diagnostics;
using System.Xml.Linq;

namespace PackagePilot.Tests.Build;

public sealed class WindowsPackagingAcceptanceTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    private static readonly XNamespace Uap =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    private static readonly XNamespace Uap5 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";

    private static readonly XNamespace Desktop =
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10";

    private static readonly XNamespace Com =
        "http://schemas.microsoft.com/appx/manifest/com/windows10";

    private static readonly XNamespace RestrictedCapabilities =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    private static readonly XNamespace AppInstaller =
        "http://schemas.microsoft.com/appx/appinstaller/2021";

    [Fact]
    public void AppInstallerGeneratorUsesQuietDailyServicingPolicy()
    {
        string repositoryRoot = FindRepositoryRoot();
        string generatorPath = Path.Combine(repositoryRoot, "build", "New-AppInstaller.ps1");
        string outputPath = Path.Combine(
            Path.GetTempPath(),
            $"PackagePilot-packaging-test-{Guid.NewGuid():N}.appinstaller");

        try
        {
            RunPowerShellScript(
                generatorPath,
                "-Version",
                "9.8.7.6",
                "-OutputPath",
                outputPath);

            XDocument document = XDocument.Load(outputPath);
            XElement root = Assert.IsType<XElement>(document.Root);
            XElement updateSettings = Assert.Single(root.Elements(AppInstaller + "UpdateSettings"));
            XElement onLaunch = Assert.Single(updateSettings.Elements(AppInstaller + "OnLaunch"));

            Assert.Equal("24", (string?)onLaunch.Attribute("HoursBetweenUpdateChecks"));
            Assert.Equal("false", (string?)onLaunch.Attribute("ShowPrompt"));
            Assert.Equal("false", (string?)onLaunch.Attribute("UpdateBlocksActivation"));
            Assert.Single(updateSettings.Elements(AppInstaller + "AutomaticBackgroundTask"));

            XElement mainBundle = Assert.Single(root.Elements(AppInstaller + "MainBundle"));
            Assert.Null(mainBundle.Attribute("ProcessorArchitecture"));

            HashSet<string> runtimeArchitectures = root
                .Element(AppInstaller + "Dependencies")!
                .Elements(AppInstaller + "Package")
                .Select(package => (string?)package.Attribute("ProcessorArchitecture"))
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "x64", "arm64" },
                runtimeArchitectures);

            XElement manifestIdentity = LoadManifest(repositoryRoot).Root!
                .Element(Foundation + "Identity")!;

            // Production identity values come from the trusted signing decision. This
            // test deliberately verifies consistency rather than inventing a publisher.
            Assert.Equal(
                (string?)manifestIdentity.Attribute("Name"),
                (string?)mainBundle.Attribute("Name"));
            Assert.Equal(
                (string?)manifestIdentity.Attribute("Publisher"),
                (string?)mainBundle.Attribute("Publisher"));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void ManifestRegistersReadOnlyShellNotificationAndBackgroundIntegrations()
    {
        XDocument manifest = LoadManifest(FindRepositoryRoot());
        XElement package = Assert.IsType<XElement>(manifest.Root);
        XElement application = Assert.Single(
            package.Element(Foundation + "Applications")!.Elements(Foundation + "Application"));
        XElement applicationExtensions = Assert.Single(
            application.Elements(Foundation + "Extensions"));

        XElement protocolExtension = Assert.Single(
            applicationExtensions.Elements(Uap + "Extension"),
            element => HasAttribute(element, "Category", "windows.protocol"));
        XElement protocol = Assert.Single(protocolExtension.Elements(Uap + "Protocol"));
        Assert.Equal("packagepilot", (string?)protocol.Attribute("Name"));

        XElement aliasExtension = Assert.Single(
            applicationExtensions.Elements(Uap5 + "Extension"),
            element => HasAttribute(element, "Category", "windows.appExecutionAlias"));
        XElement executionAlias = Assert.Single(
            aliasExtension.Descendants(Uap5 + "ExecutionAlias"));
        Assert.Equal("packagepilot.exe", (string?)executionAlias.Attribute("Alias"));

        XElement notificationExtension = Assert.Single(
            applicationExtensions.Elements(Desktop + "Extension"),
            element => HasAttribute(element, "Category", "windows.toastNotificationActivation"));
        XElement toastActivation = Assert.Single(
            notificationExtension.Elements(Desktop + "ToastNotificationActivation"));
        string notificationClassId = Assert.IsType<XAttribute>(
            toastActivation.Attribute("ToastActivatorCLSID")).Value;

        XElement notificationServer = Assert.Single(
            FindExeServers(applicationExtensions),
            server => string.Equals(
                (string?)server.Attribute("Arguments"),
                "----AppNotificationActivated:",
                StringComparison.Ordinal));
        Assert.Contains(
            notificationServer.Descendants(Com + "Class"),
            type => string.Equals(
                (string?)type.Attribute("Id"),
                notificationClassId,
                StringComparison.OrdinalIgnoreCase));

        XElement backgroundExtension = Assert.Single(
            applicationExtensions.Elements(Foundation + "Extension"),
            element => HasAttribute(element, "Category", "windows.backgroundTasks"));
        Assert.Equal(
            "Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.Task",
            (string?)backgroundExtension.Attribute("EntryPoint"));
        XElement backgroundTask = Assert.Single(
            backgroundExtension
                .Element(Foundation + "BackgroundTasks")!
                .Elements(Foundation + "Task"));
        Assert.Equal("general", (string?)backgroundTask.Attribute("Type"));

        XElement backgroundServer = Assert.Single(
            FindExeServers(applicationExtensions),
            server => string.Equals(
                (string?)server.Attribute("Executable"),
                "PackagePilot.Background.exe",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal("-RegisterForBGTaskServer", (string?)backgroundServer.Attribute("Arguments"));
        Assert.Single(backgroundServer.Descendants(Com + "Class"));

        XElement packageExtensions = Assert.Single(
            package.Elements(Foundation + "Extensions"));
        XElement inProcessExtension = Assert.Single(
            packageExtensions.Elements(Foundation + "Extension"),
            element => HasAttribute(
                element,
                "Category",
                "windows.activatableClass.inProcessServer"));
        XElement inProcessServer = Assert.Single(
            inProcessExtension.Elements(Foundation + "InProcessServer"));
        Assert.Equal(
            "Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll",
            inProcessServer.Element(Foundation + "Path")?.Value);
        Assert.Contains(
            inProcessServer.Elements(Foundation + "ActivatableClass"),
            element => string.Equals(
                (string?)element.Attribute("ActivatableClassId"),
                "Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.Task",
                StringComparison.Ordinal));

        XElement capabilities = Assert.Single(package.Elements(Foundation + "Capabilities"));
        Assert.Contains(
            capabilities.Elements(RestrictedCapabilities + "Capability"),
            capability => HasAttribute(capability, "Name", "packageQuery"));
    }

    [Fact]
    public void AppProjectTargetsX64AndArm64()
    {
        string projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "PackagePilot.App",
            "PackagePilot.App.csproj");
        XDocument project = XDocument.Load(projectPath);
        XElement root = Assert.IsType<XElement>(project.Root);

        HashSet<string> platforms = root
            .Descendants("Platforms")
            .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(platform => platform.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("x64", platforms);
        Assert.Contains("ARM64", platforms);

        HashSet<string> runtimeIdentifiers = root
            .Descendants("RuntimeIdentifier")
            .Select(element => element.Value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("win-x64", runtimeIdentifiers);
        Assert.Contains("win-arm64", runtimeIdentifiers);

        HashSet<string> platformTargets = root
            .Descendants("PlatformTarget")
            .Select(element => element.Value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("x64", platformTargets);
        Assert.Contains("ARM64", platformTargets);
    }

    private static IEnumerable<XElement> FindExeServers(XElement applicationExtensions) =>
        applicationExtensions
            .Elements(Com + "Extension")
            .Where(element => HasAttribute(element, "Category", "windows.comServer"))
            .Descendants(Com + "ExeServer");

    private static bool HasAttribute(XElement element, string name, string expected) =>
        string.Equals((string?)element.Attribute(name), expected, StringComparison.Ordinal);

    private static XDocument LoadManifest(string repositoryRoot) =>
        XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.App",
            "Package.appxmanifest"));

    private static string FindRepositoryRoot()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("PACKAGEPILOT_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot) && IsRepositoryRoot(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        foreach (string startingPath in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(startingPath); directory is not null; directory = directory.Parent)
            {
                if (IsRepositoryRoot(directory.FullName))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Package Pilot repository root. Set PACKAGEPILOT_REPO_ROOT when running tests outside the repository.");
    }

    private static bool IsRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, "PackagePilot.slnx")) &&
        File.Exists(Path.Combine(path, "src", "PackagePilot.App", "Package.appxmanifest"));

    private static void RunPowerShellScript(string scriptPath, params string[] arguments)
    {
        string windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        string executable = File.Exists(windowsPowerShell) ? windowsPowerShell : "pwsh";

        ProcessStartInfo startInfo = new(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        if (File.Exists(windowsPowerShell))
        {
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
        }

        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell for the packaging acceptance test.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"App Installer generation failed with exit code {process.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{standardOutput}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{standardError}");
    }
}
