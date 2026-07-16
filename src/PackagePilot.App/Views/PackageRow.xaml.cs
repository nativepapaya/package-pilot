using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PackagePilot.App.Views;

public sealed partial class PackageRow : UserControl
{
    public static readonly DependencyProperty PackageNameProperty = DependencyProperty.Register(
        nameof(PackageName),
        typeof(string),
        typeof(PackageRow),
        new PropertyMetadata(string.Empty, OnActionAutomationPropertyChanged));
    public static readonly DependencyProperty PublisherProperty = DependencyProperty.Register(
        nameof(Publisher), typeof(string), typeof(PackageRow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty PackageIdProperty = DependencyProperty.Register(
        nameof(PackageId), typeof(string), typeof(PackageRow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(PackageRow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(string), typeof(PackageRow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(PackageRow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty StateGlyphProperty = DependencyProperty.Register(
        nameof(StateGlyph),
        typeof(string),
        typeof(PackageRow),
        new PropertyMetadata(string.Empty, OnStateVisualPropertyChanged));
    public static readonly DependencyProperty IsPositiveStateProperty = DependencyProperty.Register(
        nameof(IsPositiveState),
        typeof(bool),
        typeof(PackageRow),
        new PropertyMetadata(false, OnStateVisualPropertyChanged));
    public static readonly DependencyProperty ActionLabelProperty = DependencyProperty.Register(
        nameof(ActionLabel),
        typeof(string),
        typeof(PackageRow),
        new PropertyMetadata("Install", OnActionAutomationPropertyChanged));
    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
        nameof(IconGlyph), typeof(string), typeof(PackageRow), new PropertyMetadata("\uE896"));
    public static readonly DependencyProperty IconUriProperty = DependencyProperty.Register(
        nameof(IconUri), typeof(Uri), typeof(PackageRow), new PropertyMetadata(null));
    public static readonly DependencyProperty IsActionEnabledProperty = DependencyProperty.Register(
        nameof(IsActionEnabled), typeof(bool), typeof(PackageRow), new PropertyMetadata(true));

    public PackageRow()
    {
        InitializeComponent();
        UpdateActionAutomationName();
        UpdateStateVisual();
    }

    public string PackageName { get => (string)GetValue(PackageNameProperty); set => SetValue(PackageNameProperty, value); }
    public string Publisher { get => (string)GetValue(PublisherProperty); set => SetValue(PublisherProperty, value); }
    public string PackageId { get => (string)GetValue(PackageIdProperty); set => SetValue(PackageIdProperty, value); }
    public string Source { get => (string)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string Version { get => (string)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public string Status { get => (string)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string StateGlyph { get => (string)GetValue(StateGlyphProperty); set => SetValue(StateGlyphProperty, value); }
    public bool IsPositiveState { get => (bool)GetValue(IsPositiveStateProperty); set => SetValue(IsPositiveStateProperty, value); }
    public string ActionLabel { get => (string)GetValue(ActionLabelProperty); set => SetValue(ActionLabelProperty, value); }
    public string IconGlyph { get => (string)GetValue(IconGlyphProperty); set => SetValue(IconGlyphProperty, value); }
    public Uri? IconUri { get => (Uri?)GetValue(IconUriProperty); set => SetValue(IconUriProperty, value); }
    public bool IsActionEnabled { get => (bool)GetValue(IsActionEnabledProperty); set => SetValue(IsActionEnabledProperty, value); }
    public string ActionAutomationName => $"{ActionLabel} {PackageName}";

    public event EventHandler? ActionInvoked;

    private void OnActionClick(object sender, RoutedEventArgs e) => ActionInvoked?.Invoke(this, EventArgs.Empty);

    private static void OnActionAutomationPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is PackageRow row)
        {
            row.UpdateActionAutomationName();
        }
    }

    private static void OnStateVisualPropertyChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is PackageRow row)
        {
            row.UpdateStateVisual();
        }
    }

    private void UpdateActionAutomationName()
    {
        if (ActionButton is not null)
        {
            AutomationProperties.SetName(ActionButton, ActionAutomationName);
        }
    }

    private void UpdateStateVisual()
    {
        if (StateBadge is null || PlainStatusText is null)
        {
            return;
        }

        var hasState = !string.IsNullOrWhiteSpace(StateGlyph);
        StateBadge.Visibility = hasState ? Visibility.Visible : Visibility.Collapsed;
        PlainStatusText.Visibility = hasState ? Visibility.Collapsed : Visibility.Visible;
        _ = VisualStateManager.GoToState(
            this,
            IsPositiveState ? "PositiveState" : "NeutralState",
            useTransitions: false);
    }
}
