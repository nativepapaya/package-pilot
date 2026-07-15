using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PackagePilot.App.Views;

public sealed partial class SourcesPage : Page
{
    private bool _canAdd;

    public SourcesPage()
    {
        InitializeComponent();
        Sources.CollectionChanged += (_, _) => UpdateState();
        UpdateState();
    }

    public ObservableCollection<SourceManagementListItem> Sources { get; } = [];
    public event EventHandler? RefreshRequested;
    public event EventHandler? AddRequested;
    public event EventHandler<SourceCommandRequestedEventArgs>? RefreshSourceRequested;
    public event EventHandler<SourceCommandRequestedEventArgs>? RemoveRequested;
    public event EventHandler<SourceCommandRequestedEventArgs>? ResetRequested;
    public event EventHandler<SourceCommandRequestedEventArgs>? ToggleExplicitRequested;

    public void SetLoading(bool isLoading)
    {
        LoadingRing.IsActive = isLoading;
        LoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !isLoading;
        AddButton.IsEnabled = _canAdd && !isLoading;
        SourceList.IsEnabled = !isLoading;
    }

    public void SetCapabilitySummary(string summary) => CapabilityText.Text = summary;

    public void SetCanAdd(bool canAdd)
    {
        _canAdd = canAdd;
        AddButton.IsEnabled = canAdd && !LoadingRing.IsActive;
    }

    public void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBanner.Title = title;
        StatusBanner.Message = message;
        StatusBanner.Severity = severity;
        StatusBanner.IsOpen = true;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void OnAddClick(object sender, RoutedEventArgs e) => AddRequested?.Invoke(this, EventArgs.Empty);

    private void OnRefreshSourceClick(object sender, RoutedEventArgs e) =>
        Raise(sender, RefreshSourceRequested);

    private void OnRemoveClick(object sender, RoutedEventArgs e) => Raise(sender, RemoveRequested);
    private void OnResetClick(object sender, RoutedEventArgs e) => Raise(sender, ResetRequested);
    private void OnToggleExplicitClick(object sender, RoutedEventArgs e) => Raise(sender, ToggleExplicitRequested);

    private void Raise(
        object sender,
        EventHandler<SourceCommandRequestedEventArgs>? handler)
    {
        if (SourceList.IsEnabled
            && sender is FrameworkElement { Tag: string id }
            && Sources.FirstOrDefault(source => string.Equals(source.Id, id, StringComparison.Ordinal)) is { } source)
        {
            handler?.Invoke(this, new SourceCommandRequestedEventArgs(source));
        }
    }

    private void UpdateState()
    {
        SourceList.Visibility = Sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = Sources.Count switch
        {
            0 => "No sources loaded",
            1 => "1 source",
            _ => $"{Sources.Count} sources"
        };
    }
}
