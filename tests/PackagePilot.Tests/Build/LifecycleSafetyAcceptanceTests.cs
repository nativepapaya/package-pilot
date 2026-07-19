namespace PackagePilot.Tests.Build;

public sealed class LifecycleSafetyAcceptanceTests
{
    [Fact]
    public void NativeWindowVisibility_CommitsAfterNativeSuccessAndCloseFailsOpen()
    {
        string mainWindow = ReadSource("PackagePilot.App", "MainWindow.xaml.cs");
        string app = ReadSource("PackagePilot.App", "App.xaml.cs");

        string show = ExtractBlock(mainWindow, "internal bool ShowAndActivate()");
        AssertOccursBefore(show, "AppWindow.Show();", "_isVisible = true;");
        AssertOccursBefore(show, "_isVisible = true;", "ActivityChanged?.Invoke");
        Assert.Contains("return false;", show, StringComparison.Ordinal);

        string hide = ExtractBlock(mainWindow, "internal bool HideToNotificationArea()");
        AssertOccursBefore(hide, "AppWindow.Hide();", "_isVisible = false;");
        AssertOccursBefore(hide, "_isVisible = false;", "_isActive = false;");
        Assert.Contains("return false;", hide, StringComparison.Ordinal);

        string close = ExtractBlock(app, "private void OnWindowClosingRequested");
        string closeToTray = ExtractBlock(close, "if (BehaviorSettings.CloseToNotificationArea");
        AssertOccursBefore(closeToTray, "args.Cancel = true;", "_window.HideToNotificationArea()");
        Assert.Contains("if (!_window.HideToNotificationArea())", closeToTray, StringComparison.Ordinal);
        Assert.Contains("_window.ShowAndActivate();", closeToTray, StringComparison.Ordinal);
        Assert.DoesNotContain("args.Cancel = false", closeToTray, StringComparison.Ordinal);
        Assert.Contains(
            "if (!CanBeginOrderlyExit(out var blockingDestination))",
            close,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ForegroundActivation_SupersedesStaleStartupShutdown()
    {
        string app = ReadSource("PackagePilot.App", "App.xaml.cs");
        string hiddenStartup = ExtractBlock(app, "private async Task StartHiddenResidentAsync()");
        string supersession = ExtractBlock(
            app,
            "private async Task<bool> ShouldCompleteHiddenStartupShutdownAsync()");

        Assert.Equal(
            2,
            CountOccurrences(
                hiddenStartup,
                "if (await ShouldCompleteHiddenStartupShutdownAsync())"));
        Assert.Equal(2, CountOccurrences(hiddenStartup, "await ShutdownCoreAsync();"));
        Assert.Equal(
            2,
            CountOccurrences(
                supersession,
                "Volatile.Read(ref _foregroundActivationRequested)"));
        AssertOccursBefore(
            supersession,
            "if (Volatile.Read(ref _foregroundActivationRequested) != 0)",
            "await Task.Delay(TimeSpan.FromMilliseconds(250));");
        AssertOccursBefore(
            supersession,
            "await Task.Delay(TimeSpan.FromMilliseconds(250));",
            "return Volatile.Read(ref _foregroundActivationRequested) == 0");
        AssertOccursBefore(
            supersession,
            "if (Volatile.Read(ref _redirectedActivationDispatchCount) != 0)",
            "await Task.Delay(TimeSpan.FromMilliseconds(250));");

        string launched = ExtractBlock(app, "protected override void OnLaunched");
        AssertOccursBefore(
            launched,
            "Volatile.Write(ref _foregroundActivationRequested, 1);",
            "ActivateRequestAsync(request)");

        string redirected = ExtractBlock(app, "private async Task ActivateFromWindowsAsync");
        AssertOccursBefore(
            redirected,
            "if (parsed.Request is { IsStartupTaskActivation: true })",
            "Volatile.Write(ref _foregroundActivationRequested, 1);");
        AssertOccursBefore(
            redirected,
            "Volatile.Write(ref _foregroundActivationRequested, 1);",
            "await ActivateRequestAsync(request);");

        string redirectedCallback = ExtractBlock(
            app,
            "private void OnAppInstanceActivated(AppActivationArguments args)");
        AssertOccursBefore(
            redirectedCallback,
            "Volatile.Write(ref _foregroundActivationRequested, 1);",
            "_dispatcherQueue?.TryEnqueue");
        AssertOccursBefore(
            redirectedCallback,
            "Interlocked.Increment(ref _redirectedActivationDispatchCount);",
            "_dispatcherQueue?.TryEnqueue");
        Assert.Contains("if (!enqueued)", redirectedCallback, StringComparison.Ordinal);
        Assert.Contains("Program.ShowFatalApplicationError", redirectedCallback, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleInstanceShutdown_ReleasesOwnershipBeforeDetachingAndRejectsLateActivation()
    {
        string program = ReadSource("PackagePilot.App", "Program.cs");
        string shutdown = ExtractBlock(program, "internal static void BeginShutdown()");

        AssertOccursBefore(
            shutdown,
            "_shutdownStarted = true;",
            "mainInstance?.UnregisterKey();");
        AssertOccursBefore(
            shutdown,
            "mainInstance?.UnregisterKey();",
            "mainInstance.Activated -= OnAppInstanceActivated;");
        AssertOccursBefore(
            shutdown,
            "mainInstance.Activated -= OnAppInstanceActivated;",
            "_redirectedActivationHandler = null;");

        string activation = ExtractBlock(
            program,
            "private static void OnAppInstanceActivated(");
        Assert.Contains("rejectedDuringShutdown = _shutdownStarted;", activation, StringComparison.Ordinal);
        AssertOccursBefore(
            activation,
            "if (rejectedDuringShutdown)",
            "PendingActivations.Enqueue(activation);");
        Assert.Contains("ShowFatalApplicationError", activation, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupAndResidentTasks_AreSupervisedAndJoinedDuringShutdown()
    {
        string app = ReadSource("PackagePilot.App", "App.xaml.cs");
        string launched = ExtractBlock(app, "protected override void OnLaunched");

        Assert.Matches(
            @"ObserveTask\s*\(\s*StartHiddenResidentAsync\(\)\s*,\s*terminateWhenUnreachable:\s*true\s*\)",
            launched);
        Assert.Matches(
            @"ObserveTask\s*\(\s*ShowInitialWindowAsync\(parsed\.Error\)\s*,[\s\S]*?terminateWhenUnreachable:\s*true\s*\)",
            launched);
        Assert.DoesNotContain("_ = StartHiddenResidentAsync()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = ShowInitialWindowAsync", app, StringComparison.Ordinal);

        string ensureResident = ExtractBlock(
            app,
            "private Task EnsureResidentInitializationAsync");
        Assert.Contains(
            "_residentInitialization ??= InitializeResidentServicesAsync();",
            ensureResident,
            StringComparison.Ordinal);

        string initializeResident = ExtractBlock(
            app,
            "private async Task InitializeResidentServicesAsync()");
        Assert.Contains(
            "EnsureBackgroundMonitoringAsync(_residentLifetime.Token)",
            initializeResident,
            StringComparison.Ordinal);

        string shutdown = ExtractBlock(
            app,
            "private async Task ShutdownCoreImplementationAsync()");
        AssertOccursBefore(
            shutdown,
            "_residentLifetime.Cancel();",
            "await _residentInitialization.WaitAsync(TimeSpan.FromSeconds(5));");
        AssertOccursBefore(shutdown, "await _operationQueue.DisposeAsync();", "Program.BeginShutdown();");

        string observer = ExtractBlock(app, "private async Task ObserveTaskCoreAsync");
        AssertOccursBefore(
            observer,
            "CanBeginOrderlyExit(out _)",
            "ForceExitAfterUnhandledFailure();");
    }

    [Fact]
    public void NotificationAreaService_UsesBroadcastRecoveryWithRetryAndFailureEvents()
    {
        string service = ReadSource(
            "PackagePilot.Windows",
            "Services",
            "WindowsNotificationAreaIconService.cs");
        string constructor = ExtractBlock(
            service,
            "public WindowsNotificationAreaIconService(string? iconPath = null)");

        AssertOccursBefore(
            constructor,
            "_taskbarCreatedMessage = RegisterWindowMessageW(TaskbarCreatedMessageName);",
            "if (_taskbarCreatedMessage == 0)");
        Assert.Contains("HwndMessage", constructor, StringComparison.Ordinal);
        Assert.Contains("_broadcastWindowHandle = CreateWindowExW", constructor, StringComparison.Ordinal);
        Assert.Contains("WsExToolWindow | WsExNoActivate", constructor, StringComparison.Ordinal);
        Assert.Contains(
            "Instances[_broadcastWindowHandle]",
            constructor,
            StringComparison.Ordinal);

        string update = ExtractBlock(
            service,
            "public NotificationAreaIconResult Update(NotificationAreaIconOptions options)");
        AssertOccursBefore(update, "_isVisible = false;", "NotificationAreaAvailabilityReason.UpdateFailed");

        string windowMessage = ExtractBlock(
            service,
            "internal nint ProcessWindowMessage");
        Assert.Contains("var result = AddIcon();", windowMessage, StringComparison.Ordinal);
        Assert.Contains(
            "PostMessageW(_broadcastWindowHandle, RestoreIconMessage",
            windowMessage,
            StringComparison.Ordinal);
        Assert.Contains("case RestoreIconMessage:", windowMessage, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(
                windowMessage,
                "NotificationAreaAvailabilityReason.ExplorerRestartRecovered") >= 2);
        Assert.True(
            CountOccurrences(
                windowMessage,
                "NotificationAreaAvailabilityReason.ExplorerRestartFailed") >= 2);
    }

    [Fact]
    public void NotificationAreaLoss_DisablesResidentModeOrRestoresAReachableWindow()
    {
        string app = ReadSource("PackagePilot.App", "App.xaml.cs");
        string ensureIcon = ExtractBlock(app, "private NotificationAreaIconResult EnsureNotificationAreaIcon()");
        string availability = ExtractBlock(
            app,
            "private void OnNotificationAreaAvailabilityChanged");
        string loss = ExtractBlock(app, "private async Task HandleNotificationAreaLossAsync");
        string disposeIcon = ExtractBlock(app, "private void DisposeNotificationAreaIcon()");

        Assert.Contains(
            "_notificationAreaIcon.AvailabilityChanged += OnNotificationAreaAvailabilityChanged;",
            ensureIcon,
            StringComparison.Ordinal);
        Assert.Contains("if (args.Result.IsVisible", availability, StringComparison.Ordinal);
        Assert.Contains(
            "HandleNotificationAreaLossAsync(args.Result.Message)",
            availability,
            StringComparison.Ordinal);
        AssertOccursBefore(
            loss,
            "TryRestoreWindowAfterNotificationAreaLossAsync()",
            "DisableUnavailableNotificationAreaModeAsync");
        AssertOccursBefore(loss, "WaitForIdleAsync()", "await ShutdownCoreAsync();");
        Assert.Contains(
            "_notificationAreaIcon.AvailabilityChanged -= OnNotificationAreaAvailabilityChanged;",
            disposeIcon,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationAreaMenu_RefreshesBackgroundSnapshotWithoutPolling()
    {
        string app = ReadSource("PackagePilot.App", "App.xaml.cs");
        string service = ReadSource(
            "PackagePilot.Windows",
            "Services",
            "WindowsNotificationAreaIconService.cs");
        string ensureIcon = ExtractBlock(app, "private NotificationAreaIconResult EnsureNotificationAreaIcon()");
        string refresh = ExtractBlock(app, "private void OnNotificationAreaMenuOpening");
        string menu = ExtractBlock(service, "private void ShowContextMenu()");

        Assert.Contains(
            "_notificationAreaIcon.MenuOpening += OnNotificationAreaMenuOpening;",
            ensureIcon,
            StringComparison.Ordinal);
        Assert.Contains("new JsonUpdateSnapshotStore", refresh, StringComparison.Ordinal);
        Assert.Contains("UpdateUpdateCount", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("Timer", refresh, StringComparison.Ordinal);
        AssertOccursBefore(menu, "RaiseMenuOpening();", "BuildReviewUpdatesLabel(_options.UpdateCount)");
    }

    [Fact]
    public void IntegrationTransitions_BlockCloseBeforeWaitingForTheAsyncGate()
    {
        string app = ReadSource("PackagePilot.App", "App.xaml.cs");
        string notificationAreaSetting = ExtractBlock(
            app,
            "public async Task<NotificationAreaIconResult> SetNotificationAreaEnabledAsync");
        string startupSetting = ExtractBlock(
            app,
            "public async Task<StartupRegistrationResult> SetStartupEnabledAsync");

        AssertTransitionIsGuarded(notificationAreaSetting);
        AssertTransitionIsGuarded(startupSetting);

        string close = ExtractBlock(app, "private void OnWindowClosingRequested");
        AssertOccursBefore(
            close,
            "Volatile.Read(ref _lifetimeTransitionCount) > 0",
            "BehaviorSettings.CloseToNotificationArea");
        string transitionClose = ExtractBlock(
            close,
            "if (Volatile.Read(ref _lifetimeTransitionCount) > 0)");
        Assert.Contains("args.Cancel = true;", transitionClose, StringComparison.Ordinal);
        Assert.Contains(
            "ShowOperationsPreventExit(AppDestination.Settings);",
            transitionClose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationRegistration_RemovesHandlerOnRegistrationFailureAndShutdown()
    {
        string app = ReadSource("PackagePilot.App", "App.xaml.cs");
        string registration = ExtractBlock(app, "private void RegisterNotifications()");

        AssertOccursBefore(
            registration,
            "manager.NotificationInvoked += OnNotificationInvoked;",
            "manager.Register();");
        AssertOccursBefore(
            registration,
            "catch",
            "manager.NotificationInvoked -= OnNotificationInvoked;");

        string shutdown = ExtractBlock(
            app,
            "private async Task ShutdownCoreImplementationAsync()");
        AssertOccursBefore(
            shutdown,
            "_notificationManager.NotificationInvoked -= OnNotificationInvoked;",
            "_notificationManager.Unregister();");
        AssertOccursBefore(
            shutdown,
            "_notificationManager.Unregister();",
            "_notificationManager = null;");
    }

    [Fact]
    public void CadenceChange_RefreshesDesiredActualStatusAndCapabilitySummary()
    {
        string settings = ReadSource(
            "PackagePilot.App",
            "Views",
            "SettingsPage.xaml.cs");
        string mainPage = ReadSource("PackagePilot.App", "MainPage.xaml.cs");
        string cadence = ExtractBlock(settings, "private async void OnUpdateCadenceChanged");
        string refresh = ExtractBlock(
            settings,
            "private async Task<BackgroundMonitoringStatus> RefreshBackgroundMonitoringStatusAsync");
        string settingChanged = ExtractBlock(mainPage, "private void OnSettingChanged");

        Assert.Contains("monitoring.DesiredCadence", refresh, StringComparison.Ordinal);
        Assert.Contains("monitoring.ActualCadence", refresh, StringComparison.Ordinal);
        AssertOccursBefore(
            cadence,
            "ConfigureAsync(cadence)",
            "RefreshBackgroundMonitoringStatusAsync(showAlerts: false)");
        Assert.Contains("\"backgroundMonitoringState\"", cadence, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SetBackgroundMonitoringState(backgroundState);", settingChanged, StringComparison.Ordinal);
        Assert.Contains(
            "_uiCoordinator.Invalidate(DestinationChangeFlags.All);",
            settingChanged,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundComHost_HasNoActivationAndHardLifetimeWatchdogs()
    {
        string program = ReadSource("PackagePilot.Background", "Program.cs");
        string task = ReadSource("PackagePilot.Background", "BackgroundUpdateTask.cs");

        Assert.Contains("ActivationTimeout = TimeSpan.FromSeconds(15)", program, StringComparison.Ordinal);
        Assert.Contains("MaximumHostLifetime = TimeSpan.FromMinutes(10)", program, StringComparison.Ordinal);
        Assert.Contains("WaitHandle.WaitAny", program, StringComparison.Ordinal);
        Assert.Contains("RequestActiveTaskCancellation();", program, StringComparison.Ordinal);
        Assert.Contains("BackgroundHostStatusReporter.TryRecordAsync", program, StringComparison.Ordinal);
        Assert.DoesNotContain("ExitEvent.WaitOne();", program, StringComparison.Ordinal);

        string completion = ExtractBlock(task, "private void CompleteHost");
        string finalization = ExtractBlock(completion, "finally");
        Assert.Contains("Program.SignalExit();", finalization, StringComparison.Ordinal);
        Assert.Contains("Program.SignalActivation(RequestCancellation);", task, StringComparison.Ordinal);
        Assert.Contains("BackgroundHostStatusReporter.TryRecordAsync", task, StringComparison.Ordinal);
        Assert.Contains("TryReportProgress(taskInstance, 0)", task, StringComparison.Ordinal);
        Assert.DoesNotContain("taskInstance.Progress = status.State", task, StringComparison.Ordinal);
        Assert.Contains("exception).ConfigureAwait(false)", task, StringComparison.Ordinal);
        string run = ExtractBlock(task, "public void Run(IBackgroundTaskInstance taskInstance)");
        AssertOccursBefore(run, "Program.SignalActivation(RequestCancellation);", "taskInstance.GetDeferral();");
        Assert.Contains("RecordFailureAndSignalExitAsync(exception)", run, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundToast_UsesAtomicTagReplacementWithoutRemoveFirst()
    {
        string sink = ReadSource("PackagePilot.Background", "WindowsNotificationSink.cs");

        Assert.Equal(1, CountOccurrences(sink, "ToastNotificationManager.History.Remove("));
        Assert.Contains("Tag = WindowsIntegrationConstants.NotificationTag", sink, StringComparison.Ordinal);
        Assert.Contains("Group = WindowsIntegrationConstants.NotificationGroup", sink, StringComparison.Ordinal);
        Assert.Contains("CreateToastNotifier().Show(notification);", sink, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatesPage_BindsQueueFeedbackAndDispatchesByExplicitOperationKind()
    {
        string updatesPage = ReadSource("PackagePilot.App", "Views", "UpdatesPage.xaml");
        string mainPage = ReadSource("PackagePilot.App", "MainPage.xaml.cs");

        Assert.Contains(
            "IsActionEnabled=\"{x:Bind IsActionEnabled, Mode=OneWay}\"",
            updatesPage,
            StringComparison.Ordinal);
        Assert.Contains("Tag=\"{x:Bind WingetPackage}\"", updatesPage, StringComparison.Ordinal);
        Assert.Contains("e.Package.RequestedOperationKind ??", mainPage, StringComparison.Ordinal);
        Assert.Contains("UpdateRowProjector.Apply", mainPage, StringComparison.Ordinal);
        Assert.Contains(".Concat(ViewModel.PendingUpgradeVerifications)", mainPage, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsRestartRequiredThisBoot(item.Key)", mainPage, StringComparison.Ordinal);

        string shell = ReadSource("PackagePilot.App", "ViewModels", "ShellViewModel.cs");
        string normalizedShell = shell.ReplaceLineEndings("\n");
        Assert.Contains("SeedPendingMutationVerifications(initialQueueSnapshot)", shell, StringComparison.Ordinal);
        Assert.Contains("GetEffectiveCheckReason(reason)", shell, StringComparison.Ordinal);
        Assert.Contains("TryStartMutationRefresh()", shell, StringComparison.Ordinal);
        Assert.Contains("|| _operationBatchDepth > 0", shell, StringComparison.Ordinal);
        Assert.Contains("ShouldSuppressNonMutationSnapshot()", shell, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CompleteMutationVerification(verificationTargets)",
            shell,
            StringComparison.Ordinal);
        Assert.Contains("ReconcileUpgradeMutationVerificationAsync(", shell, StringComparison.Ordinal);
        Assert.Contains("result.Snapshot.Updates", shell, StringComparison.Ordinal);
        Assert.Contains("if (scheduleMutationVerification && !_disposed)", shell, StringComparison.Ordinal);
        Assert.Contains("if (!_operationHistoryInitialized)", shell, StringComparison.Ordinal);
        Assert.Contains(
            "public bool CanQueuePackageMutations =>\n        _operationHistoryInitialized\n        && IsReady",
            normalizedShell,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(CanQueuePackageMutations));",
            shell,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetVerificationTargetsForCurrentBoot(PackageOperationKind.Upgrade)",
            shell,
            StringComparison.Ordinal);
        Assert.Contains("installed.IsWingetInventoryHealthy", shell, StringComparison.Ordinal);
        Assert.Contains(
            "PackageOperationKind.Install or PackageOperationKind.Uninstall",
            shell,
            StringComparison.Ordinal);
        Assert.Contains("new JsonMutationVerificationStore", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingMutationVerifications.v", shell, StringComparison.Ordinal);

        string enqueueOperation = ExtractBlock(shell, "public Guid EnqueueOperation(");
        Assert.Contains("EnqueueOperations", enqueueOperation, StringComparison.Ordinal);
        string enqueueOperations = ExtractBlock(
            shell,
            "public MutationAdmissionBatchResult EnqueueOperations(");
        Assert.Contains("if (!CanQueuePackageMutations)", enqueueOperations, StringComparison.Ordinal);

        string admission = ReadSource(
            "PackagePilot.Core",
            "Services",
            "MutationOperationAdmissionService.cs");
        string admissionBatch = ExtractBlock(
            admission,
            "public MutationAdmissionBatchResult Enqueue(");
        AssertOccursBefore(
            admissionBatch,
            "_store.Save(_tracker.CreateSnapshot())",
            "_operationQueue.Enqueue(admission.Operation)");
        string verificationTracker = ReadSource(
            "PackagePilot.Core",
            "Services",
            "MutationVerificationTracker.cs");
        string normalizedVerificationTracker = verificationTracker.ReplaceLineEndings("\n");
        Assert.Contains(
            "Phase = MutationVerificationPhase.OutcomeUnknown",
            verificationTracker,
            StringComparison.Ordinal);
        Assert.Contains(
            "Guid RevisionId,\n    PackageOperationKind Kind",
            normalizedVerificationTracker,
            StringComparison.Ordinal);
        Assert.Contains(
            "MutationVerificationPhase.ApplicationRestartPending",
            verificationTracker,
            StringComparison.Ordinal);
        Assert.Contains(
            "previousVersion!.Trim()",
            verificationTracker,
            StringComparison.Ordinal);
        string verificationReconciliation = ExtractBlock(
            shell,
            "private async Task<UpgradeVerificationReconciliation> ReconcileUpgradeMutationVerificationAsync(");
        AssertOccursBefore(
            verificationReconciliation,
            "await _operationQueue.TryMarkUpgradeNoChangeDetectedAsync(",
            "_mutationVerificationTracker.CompleteVerification([target])");
        string operationQueue = ReadSource(
            "PackagePilot.Core",
            "Services",
            "OperationQueue.cs");
        string noChangeTransaction = ExtractBlock(
            operationQueue,
            "public Task<bool> TryMarkUpgradeNoChangeDetectedAsync(");
        AssertOccursBefore(
            noChangeTransaction,
            "PersistHistorySafeAsync(publishFailure: false)",
            "return committed");
        Assert.Contains("PendingUpgradeVerifications", shell, StringComparison.Ordinal);

        string bootSession = ReadSource(
            "PackagePilot.Windows",
            "Services",
            "WindowsBootSessionIdentityProvider.cs");
        Assert.Contains("BootIdAddress", bootSession, StringComparison.Ordinal);
        Assert.DoesNotContain("TickCount64", shell, StringComparison.Ordinal);
        Assert.Contains("Package operations paused", mainPage, StringComparison.Ordinal);

        string detailsPane = ReadSource("PackagePilot.App", "Views", "PackageDetailsPane.xaml");
        Assert.Contains(
            "IsEnabled=\"{x:Bind IsPrimaryActionEnabled, Mode=OneWay}\"",
            detailsPane,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdministratorRetry_IsFinalExplicitAndCarriesOnlyReviewedAgreementFingerprints()
    {
        string mainPage = ReadSource("PackagePilot.App", "MainPage.xaml.cs");
        string shell = ReadSource("PackagePilot.App", "ViewModels", "ShellViewModel.cs");
        string action = ExtractBlock(mainPage, "private async void OnPackageActionRequested");
        string agreementReview = ExtractBlock(
            mainPage,
            "private async Task<AgreementAcceptance> ConfirmAgreementsAsync");
        string sourceReconnect = ExtractBlock(
            agreementReview,
            "if (currentSourceSnapshot is not null)");
        string enqueueOperations = ExtractBlock(
            shell,
            "public MutationAdmissionBatchResult EnqueueOperations(");

        AssertOccursBefore(
            action,
            "await ConfirmAgreementsAsync(",
            "await ConfirmAdministratorRetryAsync(package, kind)");
        Assert.Contains(
            "CanRetryPackageAsAdministratorFor(package.Key, kind)",
            action,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                action,
                "ViewModel.CanRetryPackageAsAdministratorFor(package.Key, kind)"));
        AssertOccursBefore(
            action,
            "await ConfirmAdministratorRetryAsync(package, kind)",
            "var (scope, architecture) = ReadInstallPreferences();");
        AssertOccursBefore(
            action,
            "|| ViewModel.IsPackageMutationBlocked(package.Key)",
            "var (scope, architecture) = ReadInstallPreferences();");
        Assert.Contains(
            "SourceAgreementSnapshot.Create(",
            agreementReview,
            StringComparison.Ordinal);
        Assert.Contains(
            "currentSourceSnapshot.Matches(acceptedSourceFingerprint)",
            agreementReview,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectPackageAcceptingSourceAgreementsAsync(package)",
            sourceReconnect,
            StringComparison.Ordinal);
        Assert.Contains(
            "AcceptedSourceAgreementFingerprint = request.AcceptedSourceAgreements",
            enqueueOperations,
            StringComparison.Ordinal);
        Assert.Contains(
            "AcceptedPackageAgreementFingerprint = request.AcceptedPackageAgreements",
            enqueueOperations,
            StringComparison.Ordinal);
    }

    private static void AssertTransitionIsGuarded(string method)
    {
        AssertOccursBefore(
            method,
            "Interlocked.Increment(ref _lifetimeTransitionCount);",
            "await _lifetimeTransitionGate.WaitAsync(cancellationToken);");
        AssertOccursBefore(
            method,
            "_lifetimeTransitionGate.Release();",
            "Interlocked.Decrement(ref _lifetimeTransitionCount);");
    }

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
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[markerIndex..(index + 1)];
                    }

                    break;
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
        Assert.True(firstIndex < secondIndex, $"Expected '{first}' to occur before '{second}'.");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var searchIndex = 0;
        while ((searchIndex = source.IndexOf(value, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            searchIndex += value.Length;
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
