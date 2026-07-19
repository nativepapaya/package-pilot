using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PackagePilot.App.Services;
using PackagePilot.App.ViewModels;
using PackagePilot.Core.Abstractions;
using Windows.Graphics;

namespace PackagePilot.App;

/// <summary>The Mica-backed application window and custom-title-bar host.</summary>
public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private WindowsTaskbarProgressService? _taskbarProgress;
    private bool _isVisible;
    private bool _isActive;
    private readonly long _createdAt = Stopwatch.GetTimestamp();
    private int _firstFrameRecorded;

    public MainWindow(
        ShellViewModel viewModel,
        IPrivilegedSourceManagementBroker? sourceManagementBroker = null,
        IAppLifetimeController? appLifetimeController = null,
        IAppLifetimeActivityGate? lifetimeActivityGate = null,
        IOperationDiagnosticsService? operationDiagnosticsService = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1180, 760));

        MainPage = new MainPage(
            _viewModel,
            sourceManagementBroker,
            appLifetimeController,
            lifetimeActivityGate,
            operationDiagnosticsService);
        RootFrame.Content = MainPage;
        RootFrame.Loaded += OnRootFrameLoaded;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.PendingOperations.CollectionChanged += OnPendingOperationsChanged;
        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    public MainPage MainPage { get; }
    internal bool IsVisible => _isVisible;
    internal bool IsActive => _isActive;
    internal bool IsVisibleAndActive => _isVisible && _isActive;
    internal event EventHandler<CancelEventArgs>? ClosingRequested;
    internal event EventHandler? ActivityChanged;

    private void OnRootFrameLoaded(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _firstFrameRecorded, 1) != 0)
        {
            return;
        }

        RootFrame.Loaded -= OnRootFrameLoaded;
        PackagePilotUiEventSource.Log.FirstFrame(
            Stopwatch.GetElapsedTime(_createdAt).TotalMilliseconds);
    }

    internal bool ShowAndActivate()
    {
        try
        {
            AppWindow.Show();
            _isVisible = true;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                Activate();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // The window is still a safe reopen affordance even if Windows denies focus.
            }

            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    internal bool HideToNotificationArea()
    {
        try
        {
            AppWindow.Hide();
            _isVisible = false;
            _isActive = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    /// <summary>Initializes optional Explorer integration after the HWND is active.</summary>
    internal void InitializeTaskbarProgress()
    {
        _taskbarProgress ??= new WindowsTaskbarProgressService(this);
        UpdateTaskbarProgress();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.CurrentOperation)
            or nameof(ShellViewModel.HasActiveOperation))
        {
            UpdateTaskbarProgress();
        }
    }

    private void OnPendingOperationsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateTaskbarProgress();

    private void UpdateTaskbarProgress() =>
        _taskbarProgress?.Update(_viewModel.CurrentOperation, _viewModel.PendingOperations.Count);

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var request = new CancelEventArgs();
        ClosingRequested?.Invoke(this, request);
        args.Cancel = request.Cancel;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isVisible = false;
        _isActive = false;
        AppWindow.Closing -= OnAppWindowClosing;
        Activated -= OnActivated;
        Closed -= OnClosed;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PendingOperations.CollectionChanged -= OnPendingOperationsChanged;
        _taskbarProgress?.Update(null, 0);
        _taskbarProgress?.Dispose();
        _taskbarProgress = null;
    }

    private static bool IsRecoverable(Exception exception) => exception is not
        OutOfMemoryException and not
        StackOverflowException and not
        AccessViolationException;
}
