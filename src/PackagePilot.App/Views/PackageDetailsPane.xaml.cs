using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PackagePilot.App.Views;

public sealed partial class PackageDetailsPane : UserControl
{
    private static readonly PropertyMetadata EmptyText = new(string.Empty);

    public static readonly DependencyProperty PackageNameProperty = DependencyProperty.Register(nameof(PackageName), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty PublisherProperty = DependencyProperty.Register(nameof(Publisher), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty PackageIdProperty = DependencyProperty.Register(nameof(PackageId), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty LicenseProperty = DependencyProperty.Register(nameof(License), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty ArchitectureProperty = DependencyProperty.Register(nameof(Architecture), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.Register(nameof(Scope), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty ElevationProperty = DependencyProperty.Register(nameof(Elevation), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(nameof(Tags), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty VersionsProperty = DependencyProperty.Register(nameof(Versions), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty ReleaseNotesProperty = DependencyProperty.Register(nameof(ReleaseNotes), typeof(string), typeof(PackageDetailsPane), EmptyText);
    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(nameof(IconGlyph), typeof(string), typeof(PackageDetailsPane), new PropertyMetadata("\uE896"));
    public static readonly DependencyProperty IconUriProperty = DependencyProperty.Register(nameof(IconUri), typeof(Uri), typeof(PackageDetailsPane), new PropertyMetadata(null));
    public static readonly DependencyProperty PrimaryActionLabelProperty = DependencyProperty.Register(nameof(PrimaryActionLabel), typeof(string), typeof(PackageDetailsPane), new PropertyMetadata("Install"));

    public PackageDetailsPane() => InitializeComponent();

    public string PackageName { get => (string)GetValue(PackageNameProperty); set => SetValue(PackageNameProperty, value); }
    public string Publisher { get => (string)GetValue(PublisherProperty); set => SetValue(PublisherProperty, value); }
    public string PackageId { get => (string)GetValue(PackageIdProperty); set => SetValue(PackageIdProperty, value); }
    public string Source { get => (string)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string Version { get => (string)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string License { get => (string)GetValue(LicenseProperty); set => SetValue(LicenseProperty, value); }
    public string Architecture { get => (string)GetValue(ArchitectureProperty); set => SetValue(ArchitectureProperty, value); }
    public string Scope { get => (string)GetValue(ScopeProperty); set => SetValue(ScopeProperty, value); }
    public string Elevation { get => (string)GetValue(ElevationProperty); set => SetValue(ElevationProperty, value); }
    public string Tags { get => (string)GetValue(TagsProperty); set => SetValue(TagsProperty, value); }
    public string Versions { get => (string)GetValue(VersionsProperty); set => SetValue(VersionsProperty, value); }
    public string ReleaseNotes { get => (string)GetValue(ReleaseNotesProperty); set => SetValue(ReleaseNotesProperty, value); }
    public string IconGlyph { get => (string)GetValue(IconGlyphProperty); set => SetValue(IconGlyphProperty, value); }
    public Uri? IconUri { get => (Uri?)GetValue(IconUriProperty); set => SetValue(IconUriProperty, value); }
    public string PrimaryActionLabel { get => (string)GetValue(PrimaryActionLabelProperty); set => SetValue(PrimaryActionLabelProperty, value); }

    public event EventHandler? PrimaryActionInvoked;
    public event EventHandler? CloseRequested;

    public void ShowAgreementNotice(bool show) => AgreementNotice.IsOpen = show;

    public void ShowPackage(PackageListItem package)
    {
        PackageName = package.Name;
        Publisher = package.Publisher;
        PackageId = package.PackageId;
        Source = package.Source;
        Version = package.VersionLabel;
        Description = package.Description;
        License = package.License;
        Architecture = package.Architecture;
        Scope = package.Scope;
        Elevation = package.ElevationLabel;
        Tags = package.Tags;
        Versions = package.Versions;
        ReleaseNotes = package.ReleaseNotes;
        IconGlyph = package.IconGlyph;
        IconUri = package.IconUri;
        PrimaryActionLabel = package.ActionLabel;
        SetLink(HomepageLink, package.HomepageUri);
        SetLink(PublisherLink, package.PublisherUri);
        SetLink(SupportLink, package.SupportUri);
        SetLink(LicenseLink, package.LicenseUri);
        SetLink(ReleaseNotesLink, package.ReleaseNotesUri);
        LinksPanel.Visibility = package.HomepageUri is not null
            || package.PublisherUri is not null
            || package.SupportUri is not null
            || package.LicenseUri is not null
            || package.ReleaseNotesUri is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        ReleaseNotesPanel.Visibility = string.IsNullOrWhiteSpace(package.ReleaseNotes)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static void SetLink(HyperlinkButton button, Uri? uri)
    {
        button.NavigateUri = uri;
        button.Visibility = uri is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPrimaryActionClick(object sender, RoutedEventArgs e) => PrimaryActionInvoked?.Invoke(this, EventArgs.Empty);
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
