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

        XElement startupExtension = Assert.Single(
            applicationExtensions.Elements(Desktop + "Extension"),
            element => HasAttribute(element, "Category", "windows.startupTask"));
        Assert.Equal("PackagePilot.App.exe", (string?)startupExtension.Attribute("Executable"));
        Assert.Equal(
            "Windows.FullTrustApplication",
            (string?)startupExtension.Attribute("EntryPoint"));
        XElement startupTask = Assert.Single(startupExtension.Elements(Desktop + "StartupTask"));
        Assert.Equal("PackagePilot.Startup", (string?)startupTask.Attribute("TaskId"));
        Assert.Equal("false", (string?)startupTask.Attribute("Enabled"));
        Assert.Equal("Package Pilot", (string?)startupTask.Attribute("DisplayName"));
        Assert.Null(startupTask.Attribute("ImmediateRegistration"));

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
        Assert.Contains(
            capabilities.Elements(RestrictedCapabilities + "Capability"),
            capability => HasAttribute(capability, "Name", "allowElevation"));
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

    [Fact]
    public void AppResolvesInstancingBeforeXamlAndKeepsStartupActivationLazy()
    {
        string repositoryRoot = FindRepositoryRoot();
        string appDirectory = Path.Combine(repositoryRoot, "src", "PackagePilot.App");
        string projectText = File.ReadAllText(
            Path.Combine(appDirectory, "PackagePilot.App.csproj"));
        string programText = File.ReadAllText(Path.Combine(appDirectory, "Program.cs"));
        string appText = File.ReadAllText(Path.Combine(appDirectory, "App.xaml.cs"));

        Assert.Contains("DISABLE_XAML_GENERATED_MAIN", projectText, StringComparison.Ordinal);

        int registrationIndex = programText.IndexOf(
            "AppInstance.FindOrRegisterForKey",
            StringComparison.Ordinal);
        int xamlStartIndex = programText.IndexOf("Application.Start", StringComparison.Ordinal);
        Assert.True(registrationIndex >= 0, "Program must register the primary app instance.");
        Assert.True(
            xamlStartIndex > registrationIndex,
            "Instance redirection must happen before the XAML application starts.");
        Assert.Contains("UnregisterKey()", programText, StringComparison.Ordinal);

        Assert.Contains(
            "protected override void OnLaunched",
            appText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "protected override async void OnLaunched",
            appText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RunIdentityMigrationAsync", appText, StringComparison.Ordinal);

        int startupActivationIndex = appText.IndexOf(
            "IsStartupTaskActivation: true",
            StringComparison.Ordinal);
        int startupResidentIndex = appText.IndexOf(
            "StartHiddenResidentAsync",
            startupActivationIndex,
            StringComparison.Ordinal);
        int foregroundGraphIndex = appText.IndexOf(
            "private MainWindow EnsureWindow()",
            StringComparison.Ordinal);
        int wingetConstructionIndex = appText.IndexOf(
            "new WingetClient(",
            StringComparison.Ordinal);
        Assert.True(startupActivationIndex >= 0, "OnLaunched must recognize startup-task activation.");
        Assert.True(
            startupResidentIndex > startupActivationIndex,
            "Startup-task activation must select the hidden resident path.");
        Assert.True(
            wingetConstructionIndex > foregroundGraphIndex,
            "WinGet must only be constructed inside the lazy foreground graph.");

        int diagnosticsConstructionIndex = appText.IndexOf(
            "new WindowsOperationDiagnosticsService(",
            StringComparison.Ordinal);
        Assert.True(
            diagnosticsConstructionIndex > foregroundGraphIndex,
            "Operation diagnostics must only be constructed inside the lazy foreground graph.");

        int activationMethodIndex = appText.IndexOf(
            "private async Task ActivateRequestAsync",
            StringComparison.Ordinal);
        int windowActivationIndex = appText.IndexOf(
            "if (!window.ShowAndActivate())",
            activationMethodIndex,
            StringComparison.Ordinal);
        int postLaunchIndex = appText.IndexOf(
            "EnsureResidentInitializationAsync(configureJumpList: true)",
            windowActivationIndex,
            StringComparison.Ordinal);
        Assert.True(windowActivationIndex > activationMethodIndex, "Foreground activation must show the window.");
        Assert.True(
            postLaunchIndex > windowActivationIndex,
            "The main window must be active before asynchronous shell work begins.");

        Assert.Contains("RedirectTimeoutMilliseconds = 5_000", programText, StringComparison.Ordinal);
        Assert.Contains("MaximumPendingActivations", programText, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityDiagnosticsAreExplicitLazyAndAccessible()
    {
        string repositoryRoot = FindRepositoryRoot();
        string appDirectory = Path.Combine(repositoryRoot, "src", "PackagePilot.App");
        string activityXaml = File.ReadAllText(Path.Combine(
            appDirectory,
            "Views",
            "ActivityPage.xaml"));
        string activityCode = File.ReadAllText(Path.Combine(
            appDirectory,
            "Views",
            "ActivityPage.xaml.cs"));
        string mainPage = File.ReadAllText(Path.Combine(appDirectory, "MainPage.xaml.cs"));

        Assert.Contains(
            "x:Load=\"{x:Bind CanViewDiagnostic}\"",
            activityXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{x:Bind DiagnosticAutomationName}\"",
            activityXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToolTipService.ToolTip=\"{x:Bind DiagnosticToolTip}\"",
            activityXaml,
            StringComparison.Ordinal);
        Assert.Contains("OnViewDiagnosticClick", activityCode, StringComparison.Ordinal);

        int clickHandler = mainPage.IndexOf(
            "private async void OnViewDiagnosticRequested",
            StringComparison.Ordinal);
        int diagnosticRead = mainPage.IndexOf(
            "_operationDiagnosticsService.ReadAsync(",
            clickHandler,
            StringComparison.Ordinal);
        Assert.True(clickHandler >= 0, "MainPage must handle an explicit Activity diagnostic request.");
        Assert.True(
            diagnosticRead > clickHandler,
            "Provider diagnostics must only be read from the explicit Activity click handler.");
        Assert.Contains(
            "if (!ViewModel.ClearHistory()",
            mainPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeleteOwnedLogsAsync(diagnostics)",
            mainPage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoverActionsUseExactPackageKeysAndLiveEnabledState()
    {
        string repositoryRoot = FindRepositoryRoot();
        string discoverXaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.App",
            "Views",
            "DiscoverPage.xaml"));
        string discoverCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.App",
            "Views",
            "DiscoverPage.xaml.cs"));

        Assert.Contains(
            "Tag=\"{x:Bind WingetPackage}\"",
            discoverXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Tag=\"{x:Bind PackageId}\"",
            discoverXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsActionEnabled=\"{x:Bind IsActionEnabled, Mode=OneWay}\"",
            discoverXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ActionLabel=\"{x:Bind ActionLabel, Mode=OneWay}\"",
            discoverXaml,
            StringComparison.Ordinal);
        Assert.Contains("row.Tag is PackageKey packageKey", discoverCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MutationDiagnosticsUseSupportedExactProviderReferences()
    {
        string repositoryRoot = FindRepositoryRoot();
        string servicesDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.Windows",
            "Services");
        string wingetClient = File.ReadAllText(Path.Combine(servicesDirectory, "WingetClient.cs"));
        string msixClient = File.ReadAllText(Path.Combine(
            servicesDirectory,
            "WindowsMsixPackageOperationClient.cs"));
        string diagnostics = File.ReadAllText(Path.Combine(
            servicesDirectory,
            "WindowsOperationDiagnosticsService.cs"));
        string diagnosticFiles = File.ReadAllText(Path.Combine(
            servicesDirectory,
            "OperationDiagnosticFiles.cs"));

        Assert.Contains("CorrelationData = CreateCorrelationData(operation.Id)", wingetClient, StringComparison.Ordinal);
        Assert.DoesNotContain("LogOutputPath", wingetClient, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPrepareWingetInstallerLog", wingetClient, StringComparison.Ordinal);
        Assert.DoesNotContain("FinalizeWingetInstallerLog", wingetClient, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPrepareWingetInstallerLog", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("FinalizeWingetInstallerLog", diagnostics, StringComparison.Ordinal);
        Assert.Contains("result.ActivityId", msixClient, StringComparison.Ordinal);
        Assert.Contains("OperationDiagnosticProvider.WindowsDeployment", msixClient, StringComparison.Ordinal);
        Assert.Contains("WinGetCOM-*.log", diagnosticFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", diagnosticFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.CreateDirectory", diagnosticFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("FileOptions.WriteThrough", diagnosticFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("powershell", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("winget.exe", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageElevationUsesAnIsolatedAuthenticatedOneShotBoundary()
    {
        string repositoryRoot = FindRepositoryRoot();
        string coreDirectory = Path.Combine(repositoryRoot, "src", "PackagePilot.Core");
        string windowsServices = Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.Windows",
            "Services");
        string requestModel = File.ReadAllText(Path.Combine(
            coreDirectory,
            "Models",
            "PackageAdminModels.cs"));
        string protocol = File.ReadAllText(Path.Combine(
            coreDirectory,
            "Services",
            "PackageAdminPipeProtocol.cs"));
        string sourceProtocol = File.ReadAllText(Path.Combine(
            coreDirectory,
            "Services",
            "SourceAdminPipeProtocol.cs"));
        string broker = File.ReadAllText(Path.Combine(
            windowsServices,
            "ElevatedPackageOperationBroker.cs"));
        string sourceBroker = File.ReadAllText(Path.Combine(
            windowsServices,
            "ElevatedSourceManagementBroker.cs"));
        string aclFactory = File.ReadAllText(Path.Combine(
            windowsServices,
            "ElevatedPipeAclFactory.cs"));
        string serverVerifier = File.ReadAllText(Path.Combine(
            windowsServices,
            "ElevatedPipeServerVerifier.cs"));
        string helper = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.PackageAdmin",
            "Program.cs"));
        string sourceHelper = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.SourceAdmin",
            "Program.cs"));
        string appProject = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.App",
            "PackagePilot.App.csproj"));
        string solution = File.ReadAllText(Path.Combine(repositoryRoot, "PackagePilot.slnx"));
        string wingetClient = File.ReadAllText(Path.Combine(windowsServices, "WingetClient.cs"));
        string packageHelperManifest = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "PackagePilot.PackageAdmin",
            "app.manifest"));

        Assert.Contains("PackagePilot.PackageAdmin.", protocol, StringComparison.Ordinal);
        Assert.Contains("PackagePilot.PackageAdmin.v1", protocol, StringComparison.Ordinal);
        Assert.Contains("PackagePilot.SourceAdmin.v1", sourceProtocol, StringComparison.Ordinal);
        Assert.DoesNotContain("PackagePilot.SourceAdmin.v1", protocol, StringComparison.Ordinal);
        Assert.Contains("UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow", protocol, StringComparison.Ordinal);
        Assert.DoesNotContain("public string Command", requestModel, StringComparison.Ordinal);
        Assert.DoesNotContain("public string Arguments", requestModel, StringComparison.Ordinal);
        Assert.DoesNotContain("public string Path", requestModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Header", requestModel, StringComparison.Ordinal);

        Assert.Contains("Verb = \"runas\"", broker, StringComparison.Ordinal);
        Assert.Contains("GetNamedPipeClientProcessId", broker, StringComparison.Ordinal);
        Assert.Contains("AuthenticateServerAsync", broker, StringComparison.Ordinal);
        Assert.Contains("requestSent = true", broker, StringComparison.Ordinal);
        Assert.Contains("WingetErrorKind.OutcomeUnknown", broker, StringComparison.Ordinal);
        Assert.Contains("Package state must be verified before retrying", broker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PrivilegedPackageRequestDispatcher.DispatchAsync", helper, StringComparison.Ordinal);
        Assert.Contains("IsElevated()", helper, StringComparison.Ordinal);
        Assert.Contains("RanAsAdministrator = ranAsAdministrator", helper, StringComparison.Ordinal);
        Assert.Contains("level=\"asInvoker\"", packageHelperManifest, StringComparison.Ordinal);
        Assert.DoesNotContain("requireAdministrator", packageHelperManifest, StringComparison.Ordinal);

        int packageServerVerification = helper.IndexOf(
            "ElevatedPipeServerVerifier.VerifyTrustedAppServer",
            StringComparison.Ordinal);
        int packageHmacAuthentication = helper.IndexOf(
            "PackageAdminPipeProtocol.AuthenticateClientAsync",
            StringComparison.Ordinal);
        int sourceServerVerification = sourceHelper.IndexOf(
            "ElevatedPipeServerVerifier.VerifyTrustedAppServer",
            StringComparison.Ordinal);
        int sourceHmacAuthentication = sourceHelper.IndexOf(
            "SourceAdminPipeProtocol.AuthenticateClientAsync",
            StringComparison.Ordinal);
        Assert.True(packageServerVerification >= 0
            && packageServerVerification < packageHmacAuthentication);
        Assert.True(sourceServerVerification >= 0
            && sourceServerVerification < sourceHmacAuthentication);
        Assert.Contains("GetNamedPipeServerProcessId", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("GetPackageFamilyName", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("GetPackageFullName", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("GetPackagePathByFullName", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("GetCurrentPackageFamilyName", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("PackageFamilyNameFromId", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("ExpectedPackageName = \"PackagePilot.Desktop\"", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("ExpectedPackagePublisher = \"CN=PackagePilot.Dev\"", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("PackagePilot.App.exe", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("\"WindowsApps\"", serverVerifier, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(serverPackageFamily)", serverVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("--parent", helper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--parent", sourceHelper, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("NamedPipeServerStreamAcl.Create", aclFactory, StringComparison.Ordinal);
        Assert.Contains("WindowsIdentity.GetCurrent", aclFactory, StringComparison.Ordinal);
        Assert.Contains("PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize", aclFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("PipeAccessRights.FullControl", aclFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("BuiltinAdministratorsSid", aclFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("WorldSid", aclFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthenticatedUserSid", aclFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentUserOnly", aclFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentUserOnly", broker, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentUserOnly", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentUserOnly", sourceBroker, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentUserOnly", sourceHelper, StringComparison.Ordinal);
        Assert.Contains("ElevatedPipeAclFactory.CreateServerForCurrentUser", sourceBroker, StringComparison.Ordinal);
        Assert.Contains("ElevatedPipeAclFactory.CreateClient", sourceHelper, StringComparison.Ordinal);

        Assert.Contains("PackagePilot.PackageAdmin.csproj", solution, StringComparison.Ordinal);
        Assert.Contains("PackagePilot.PackageAdmin.exe", appProject, StringComparison.Ordinal);
        Assert.Contains("PackagePilot.PackageAdmin.dll", appProject, StringComparison.Ordinal);
        Assert.Contains("PackagePilot.PackageAdmin.deps.json", appProject, StringComparison.Ordinal);
        Assert.Contains("PackagePilot.PackageAdmin.runtimeconfig.json", appProject, StringComparison.Ordinal);
        Assert.Contains("PackageAgreementSnapshot.Create", wingetClient, StringComparison.Ordinal);
        Assert.Contains("AcceptedPackageAgreementFingerprint", wingetClient, StringComparison.Ordinal);
        Assert.Contains("packageAgreementSnapshot.Matches", wingetClient, StringComparison.Ordinal);
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
