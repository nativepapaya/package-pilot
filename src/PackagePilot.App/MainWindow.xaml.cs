using System.Collections.Specialized;
using System.ComponentModel;
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
    private readonly WindowsTaskbarProgressService _taskbarProgress;

    public MainWindow(
        ShellViewModel viewModel,
        IPrivilegedSourceManagementBroker? sourceManagementBroker = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1180, 760));

        MainPage = new MainPage(_viewModel, sourceManagementBroker);
        RootFrame.Content = MainPage;

        _taskbarProgress = new WindowsTaskbarProgressService(this);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.PendingOperations.CollectionChanged += OnPendingOperationsChanged;
        Closed += OnClosed;
        UpdateTaskbarProgress();
    }

    public MainPage MainPage { get; }

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
        _taskbarProgress.Update(_viewModel.CurrentOperation, _viewModel.PendingOperations.Count);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PendingOperations.CollectionChanged -= OnPendingOperationsChanged;
        _taskbarProgress.Update(null, 0);
        _taskbarProgress.Dispose();
    }
}
