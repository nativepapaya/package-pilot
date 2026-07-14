using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PackagePilot.App.Views;

public sealed partial class PackageRow : UserControl
{
    public static readonly DependencyProperty PackageNameProperty = DependencyProperty.Register(
        nameof(PackageName), typeof(string), typeof(PackageRow), new PropertyMetadata(string.Empty));
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
    public static readonly DependencyProperty ActionLabelProperty = DependencyProperty.Register(
        nameof(ActionLabel), typeof(string), typeof(PackageRow), new PropertyMetadata("Install"));
    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
        nameof(IconGlyph), typeof(string), typeof(PackageRow), new PropertyMetadata("\uE896"));
    public static readonly DependencyProperty IconUriProperty = DependencyProperty.Register(
        nameof(IconUri), typeof(Uri), typeof(PackageRow), new PropertyMetadata(null));
    public static readonly DependencyProperty IsActionEnabledProperty = DependencyProperty.Register(
        nameof(IsActionEnabled), typeof(bool), typeof(PackageRow), new PropertyMetadata(true));

    public PackageRow() => InitializeComponent();

    public string PackageName { get => (string)GetValue(PackageNameProperty); set => SetValue(PackageNameProperty, value); }
    public string Publisher { get => (string)GetValue(PublisherProperty); set => SetValue(PublisherProperty, value); }
    public string PackageId { get => (string)GetValue(PackageIdProperty); set => SetValue(PackageIdProperty, value); }
    public string Source { get => (string)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string Version { get => (string)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public string Status { get => (string)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public string ActionLabel { get => (string)GetValue(ActionLabelProperty); set => SetValue(ActionLabelProperty, value); }
    public string IconGlyph { get => (string)GetValue(IconGlyphProperty); set => SetValue(IconGlyphProperty, value); }
    public Uri? IconUri { get => (Uri?)GetValue(IconUriProperty); set => SetValue(IconUriProperty, value); }
    public bool IsActionEnabled { get => (bool)GetValue(IsActionEnabledProperty); set => SetValue(IsActionEnabledProperty, value); }
    public string ActionAutomationName => $"{ActionLabel} {PackageName}";

    public event EventHandler? ActionInvoked;

    private void OnActionClick(object sender, RoutedEventArgs e) => ActionInvoked?.Invoke(this, EventArgs.Empty);
}
