using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PackagePilot.Core.Models;

namespace PackagePilot.App.Views;

public sealed partial class PackageDetailsPane : UserControl
{
    private static readonly PropertyMetadata EmptyText = new(string.Empty);
    private PackageListItem? _package;
    private bool _mutationActionsAvailable = true;
    private bool _isPackageSubscribed;

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
    public static readonly DependencyProperty IsPrimaryActionEnabledProperty = DependencyProperty.Register(nameof(IsPrimaryActionEnabled), typeof(bool), typeof(PackageDetailsPane), new PropertyMetadata(true));

    public PackageDetailsPane()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

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
    public bool IsPrimaryActionEnabled { get => (bool)GetValue(IsPrimaryActionEnabledProperty); set => SetValue(IsPrimaryActionEnabledProperty, value); }

    public event EventHandler? PrimaryActionInvoked;
    public event EventHandler? CloseRequested;

    public void ShowAgreementNotice(bool show) => AgreementNotice.IsOpen = show;

    public void ShowPackage(PackageListItem package)
    {
        ArgumentNullException.ThrowIfNull(package);
        DetachPackage();
        _package = package;
        AttachPackage();

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
        UpdatePrimaryActionEnabled();
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

    public void SetMutationActionsAvailable(bool available)
    {
        _mutationActionsAvailable = available;
        UpdatePrimaryActionEnabled();
    }

    private void OnPackagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageListItem.IsActionEnabled))
        {
            UpdatePrimaryActionEnabled();
        }
        else if (e.PropertyName == nameof(PackageListItem.ActionLabel)
            && _package is not null)
        {
            PrimaryActionLabel = _package.ActionLabel;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => AttachPackage();

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachPackage();

    private void AttachPackage()
    {
        if (_package is not null && !_isPackageSubscribed)
        {
            _package.PropertyChanged += OnPackagePropertyChanged;
            _isPackageSubscribed = true;
        }
    }

    private void DetachPackage()
    {
        if (_package is not null && _isPackageSubscribed)
        {
            _package.PropertyChanged -= OnPackagePropertyChanged;
            _isPackageSubscribed = false;
        }
    }

    private void UpdatePrimaryActionEnabled()
    {
        if (_package is null)
        {
            IsPrimaryActionEnabled = false;
            return;
        }

        var requiresMutationRecovery = _package.InstalledActionKind is null
            or InstalledAppActionKind.UninstallWithWinget
            or InstalledAppActionKind.RemoveMsix;
        IsPrimaryActionEnabled = _package.IsActionEnabled
            && (_mutationActionsAvailable || !requiresMutationRecovery);
    }

    private static void SetLink(HyperlinkButton button, Uri? uri)
    {
        button.NavigateUri = uri;
        button.Visibility = uri is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPrimaryActionClick(object sender, RoutedEventArgs e) => PrimaryActionInvoked?.Invoke(this, EventArgs.Empty);
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
