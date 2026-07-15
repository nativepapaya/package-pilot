namespace PackagePilot.Tests.Build;

public sealed class SourceLifetimeSafetyAcceptanceTests
{
    [Fact]
    public void ForegroundGraph_SharesOneLifetimeGateAcrossSourceAndExitOwners()
    {
        string app = ReadSource("PackagePilot.App", "App.xaml.cs");
        string ensureWindow = ExtractBlock(app, "private MainWindow EnsureWindow()");

        Assert.Contains(
            "private readonly IAppLifetimeActivityGate _lifetimeActivityGate",
            app,
            StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(ensureWindow, "_lifetimeActivityGate"));

        string orderlyExit = ExtractBlock(
            app,
            "private bool CanBeginOrderlyExit(out AppDestination blockingDestination)");
        AssertOccursBefore(
            orderlyExit,
            "_lifetimeActivityGate.TryBeginShutdownIfIdle(",
            "_operationQueue?.TryBeginShutdownIfIdle() ?? true");
        Assert.Contains("ToBlockingDestination(activity)", orderlyExit, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryForegroundSourceCall_IsInsideTheSharedActivityGate()
    {
        string viewModel = ReadSource(
            "PackagePilot.App",
            "ViewModels",
            "ShellViewModel.cs");

        string sourceStatuses = ExtractBlock(
            viewModel,
            "private async Task<IReadOnlyList<PackageSourceStatus>?> LoadSourceStatusesAsync()");
        AssertGatePrecedes(sourceStatuses, "_wingetClient.GetSourcesAsync");

        string managedSources = ExtractBlock(
            viewModel,
            "public async Task RefreshManagedSourcesAsync()");
        AssertGatePrecedes(
            managedSources,
            "_sourceManagementService.GetSourceManagementCapabilitiesAsync");
        AssertGatePrecedes(managedSources, "_sourceManagementService.GetSourceDetailsAsync");

        string namedRefresh = ExtractBlock(
            viewModel,
            "public async Task<SourceOperationResult?> RefreshManagedSourceAsync");
        AssertGatePrecedes(namedRefresh, "_sourceManagementService.RefreshSourceAsync");

        Assert.Equal(1, CountOccurrences(viewModel, "_wingetClient.GetSourcesAsync"));
        Assert.Equal(1, CountOccurrences(viewModel, "_sourceManagementService.GetSourceDetailsAsync"));
        Assert.Equal(1, CountOccurrences(viewModel, "_sourceManagementService.RefreshSourceAsync"));
    }

    [Fact]
    public void PrivilegedSourceMutation_IsSerializedAndRowsAreDisabledWhileBusy()
    {
        string mainPage = ReadSource("PackagePilot.App", "MainPage.xaml.cs");
        string mutation = ExtractBlock(
            mainPage,
            "private async Task ExecuteSourceMutationAsync");

        Assert.Contains("AppLifetimeActivityKind.SourceMutation", mutation, StringComparison.Ordinal);
        AssertGatePrecedes(mutation, "_sourceManagementBroker.ExecuteElevatedAsync");
        AssertOccursBefore(
            mutation,
            "activity.Dispose();",
            "await ViewModel.RefreshManagedSourcesAsync();");

        string sourcesPage = ReadSource(
            "PackagePilot.App",
            "Views",
            "SourcesPage.xaml.cs");
        string loading = ExtractBlock(sourcesPage, "public void SetLoading(bool isLoading)");
        Assert.Contains("SourceList.IsEnabled = !isLoading;", loading, StringComparison.Ordinal);
        string raise = ExtractBlock(sourcesPage, "private void Raise(");
        Assert.Contains("if (SourceList.IsEnabled", raise, StringComparison.Ordinal);
    }

    [Fact]
    public void CadenceAndResidentPreferences_BlockAllOrderlyExitPaths()
    {
        string settings = ReadSource(
            "PackagePilot.App",
            "Views",
            "SettingsPage.xaml.cs");
        string cadence = ExtractBlock(settings, "private async void OnUpdateCadenceChanged");
        AssertOccursBefore(
            cadence,
            "AppLifetimeActivityKind.WindowsIntegration",
            "_settings.Values[\"updateMonitoringCadence\"] = value;");
        AssertOccursBefore(
            cadence,
            "AppLifetimeActivityKind.WindowsIntegration",
            "_backgroundRegistration.ConfigureAsync(cadence)");

        string app = ReadSource("PackagePilot.App", "App.xaml.cs");
        string requestExit = ExtractBlock(app, "private async Task RequestExitAsync()");
        AssertOccursBefore(
            requestExit,
            "Volatile.Read(ref _lifetimeTransitionCount) > 0",
            "CanBeginOrderlyExit(out var blockingDestination)");

        string notificationArea = ExtractBlock(
            app,
            "public async Task<NotificationAreaIconResult> SetNotificationAreaEnabledAsync");
        string startup = ExtractBlock(
            app,
            "public async Task<StartupRegistrationResult> SetStartupEnabledAsync");
        Assert.Contains("AppLifetimeActivityKind.WindowsIntegration", notificationArea, StringComparison.Ordinal);
        Assert.Contains("AppLifetimeActivityKind.WindowsIntegration", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedCloseToTray_PrecedesExitOnlyActivityRefusal()
    {
        string app = ReadSource("PackagePilot.App", "App.xaml.cs");
        string close = ExtractBlock(app, "private void OnWindowClosingRequested");

        AssertOccursBefore(
            close,
            "if (BehaviorSettings.CloseToNotificationArea",
            "if (_lifetimeActivityGate.Snapshot.ActiveKind is { } activeActivity)");
        AssertOccursBefore(
            close,
            "if (_lifetimeActivityGate.Snapshot.ActiveKind is { } activeActivity)",
            "CanBeginOrderlyExit(out var blockingDestination)");
    }

    private static void AssertGatePrecedes(string source, string operation) =>
        AssertOccursBefore(source, "_lifetimeActivityGate.TryEnter(", operation);

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            Path.Combine(segments)));

    private static string ExtractBlock(string source, string marker)
    {
        int markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Could not find source marker '{marker}'.");

        int openingBrace = source.IndexOf('{', markerIndex);
        Assert.True(openingBrace >= 0, $"Could not find an opening brace after '{marker}'.");

        var depth = 0;
        for (int index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[markerIndex..(index + 1)];
            }
        }

        throw new InvalidDataException($"Source block '{marker}' was not balanced.");
    }

    private static void AssertOccursBefore(string source, string first, string second)
    {
        int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Could not find '{first}'.");
        Assert.True(secondIndex >= 0, $"Could not find '{second}'.");
        Assert.True(firstIndex < secondIndex, $"Expected '{first}' before '{second}'.");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = 0;
             (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length)
        {
            count++;
        }

        return count;
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
