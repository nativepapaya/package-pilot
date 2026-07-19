using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;

namespace PackagePilot.App.Views;

public sealed class PackageListItem : INotifyPropertyChanged
{
    private string _status = string.Empty;
    private string _name = string.Empty;
    private string _publisher = string.Empty;
    private string _iconGlyph = "\uE896";
    private Uri? _iconUri;
    private AppIconReference? _iconReference;
    private string _actionLabel = "Install";
    private string _installedVersion = string.Empty;
    private string _availableVersion = string.Empty;
    private bool _isActionEnabled = true;
    private PackageOperationState? _operationState;
    private WingetErrorKind? _operationErrorKind;
    private MutationVerificationPhase? _verificationPhase;
    private string _stateGlyph = string.Empty;
    private bool _isPositiveState;
    private bool _requiresAdministratorRetry;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Publisher { get => _publisher; set => SetProperty(ref _publisher, value); }
    public string PackageId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string InstalledVersion
    {
        get => _installedVersion;
        set
        {
            if (SetProperty(ref _installedVersion, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VersionLabel)));
            }
        }
    }
    public string AvailableVersion
    {
        get => _availableVersion;
        set
        {
            if (SetProperty(ref _availableVersion, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VersionLabel)));
            }
        }
    }
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string ActionLabel
    {
        get => _actionLabel;
        set => SetProperty(ref _actionLabel, value);
    }
    public string IconGlyph { get => _iconGlyph; set => SetProperty(ref _iconGlyph, value); }
    public Uri? IconUri { get => _iconUri; set => SetProperty(ref _iconUri, value); }
    public AppIconReference? IconReference
    {
        get => _iconReference;
        set => SetProperty(ref _iconReference, value);
    }
    public string Description { get; set; } = "Package details are not available from this source.";
    public string License { get; set; } = "Not provided";
    public string Tags { get; set; } = "Not provided";
    public string Architecture { get; set; } = "Auto";
    public string Scope { get; set; } = "Installer default";
    public string Versions { get; set; } = "Not provided";
    public string ReleaseNotes { get; set; } = string.Empty;
    public Uri? HomepageUri { get; set; }
    public Uri? PublisherUri { get; set; }
    public Uri? SupportUri { get; set; }
    public Uri? LicenseUri { get; set; }
    public Uri? ReleaseNotesUri { get; set; }
    public bool RequiresElevation { get; set; }
    public bool IsActionEnabled
    {
        get => _isActionEnabled;
        set => SetProperty(ref _isActionEnabled, value);
    }

    public PackageOperationKind? RequestedOperationKind { get; set; }
    public PackageOperationState? OperationState
    {
        get => _operationState;
        set => SetProperty(ref _operationState, value);
    }
    public WingetErrorKind? OperationErrorKind
    {
        get => _operationErrorKind;
        set => SetProperty(ref _operationErrorKind, value);
    }
    public MutationVerificationPhase? VerificationPhase
    {
        get => _verificationPhase;
        set => SetProperty(ref _verificationPhase, value);
    }
    public string StateGlyph
    {
        get => _stateGlyph;
        set => SetProperty(ref _stateGlyph, value);
    }
    public bool IsPositiveState
    {
        get => _isPositiveState;
        set => SetProperty(ref _isPositiveState, value);
    }
    public bool RequiresAdministratorRetry
    {
        get => _requiresAdministratorRetry;
        set => SetProperty(ref _requiresAdministratorRetry, value);
    }
    public string? InstalledAppId { get; set; }
    public InstalledAppActionKind? InstalledActionKind { get; set; }
    public PackageKey? WingetPackage { get; set; }
    public string? PackageFullName { get; set; }
    public Uri? ActionDestination { get; set; }

    public string VersionLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(InstalledVersion) && !string.IsNullOrWhiteSpace(AvailableVersion))
            {
                return $"{InstalledVersion}  →  {AvailableVersion}";
            }

            return !string.IsNullOrWhiteSpace(AvailableVersion) ? AvailableVersion : InstalledVersion;
        }
    }

    public string ElevationLabel => RequiresElevation ? "Administrator approval expected" : "No elevation expected";

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void ApplyOperationFeedback(PackageListItem source)
    {
        Status = source.Status;
        ActionLabel = source.ActionLabel;
        IsActionEnabled = source.IsActionEnabled;
        OperationState = source.OperationState;
        OperationErrorKind = source.OperationErrorKind;
        VerificationPhase = source.VerificationPhase;
        StateGlyph = source.StateGlyph;
        IsPositiveState = source.IsPositiveState;
        RequiresAdministratorRetry = source.RequiresAdministratorRetry;
    }

    internal void ApplyDiscoverState(PackageListItem source)
    {
        ApplyOperationFeedback(source);
        InstalledVersion = source.InstalledVersion;
        AvailableVersion = source.AvailableVersion;
        RequestedOperationKind = source.RequestedOperationKind;
    }

    internal void ApplyPresentation(PackageListItem source)
    {
        Name = source.Name;
        Publisher = source.Publisher;
        IconGlyph = source.IconGlyph;
        IconUri = source.IconUri;
        IconReference = source.IconReference;
        ApplyDiscoverState(source);
        InstalledAppId = source.InstalledAppId;
        InstalledActionKind = source.InstalledActionKind;
        WingetPackage = source.WingetPackage;
        PackageFullName = source.PackageFullName;
        ActionDestination = source.ActionDestination;
        RequiresElevation = source.RequiresElevation;
    }

    internal PackageListItemKey StableKey => new(
        PackageId,
        Source,
        InstalledAppId ?? string.Empty);

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

internal readonly record struct PackageListItemKey(
    string PackageId,
    string Source,
    string InstalledAppId);

internal static class PackageListItemComparer
{
    public static bool HaveSameRows(
        IReadOnlyList<PackageListItem> current,
        IReadOnlyList<PackageListItem> replacement) =>
        HaveSameRowsCore(current, replacement, includeOperationFeedback: true);

    public static bool HaveSameRowsExceptOperationFeedback(
        IReadOnlyList<PackageListItem> current,
        IReadOnlyList<PackageListItem> replacement) =>
        HaveSameRowsCore(current, replacement, includeOperationFeedback: false);

    private static bool HaveSameRowsCore(
        IReadOnlyList<PackageListItem> current,
        IReadOnlyList<PackageListItem> replacement,
        bool includeOperationFeedback)
    {
        if (current.Count != replacement.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = replacement[index];
            if (!string.Equals(left.PackageId, right.PackageId, StringComparison.Ordinal)
                || !string.Equals(left.Source, right.Source, StringComparison.Ordinal)
                || !string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                || !string.Equals(left.Publisher, right.Publisher, StringComparison.Ordinal)
                || !string.Equals(left.InstalledVersion, right.InstalledVersion, StringComparison.Ordinal)
                || !string.Equals(left.AvailableVersion, right.AvailableVersion, StringComparison.Ordinal)
                || (includeOperationFeedback
                    && (!string.Equals(left.Status, right.Status, StringComparison.Ordinal)
                        || !string.Equals(left.ActionLabel, right.ActionLabel, StringComparison.Ordinal)
                        || left.IsActionEnabled != right.IsActionEnabled
                        || left.OperationState != right.OperationState
                        || left.OperationErrorKind != right.OperationErrorKind
                        || left.VerificationPhase != right.VerificationPhase
                        || !string.Equals(left.StateGlyph, right.StateGlyph, StringComparison.Ordinal)
                        || left.IsPositiveState != right.IsPositiveState
                        || left.RequiresAdministratorRetry != right.RequiresAdministratorRetry))
                || !string.Equals(left.IconGlyph, right.IconGlyph, StringComparison.Ordinal)
                || left.RequiresElevation != right.RequiresElevation
                || left.RequestedOperationKind != right.RequestedOperationKind
                || left.InstalledActionKind != right.InstalledActionKind
                || left.WingetPackage != right.WingetPackage
                || !string.Equals(left.InstalledAppId, right.InstalledAppId, StringComparison.Ordinal)
                || !string.Equals(left.PackageFullName, right.PackageFullName, StringComparison.Ordinal)
                || left.ActionDestination != right.ActionDestination
                || left.IconReference != right.IconReference
                || left.IconUri != right.IconUri)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class OperationListItem : INotifyPropertyChanged
{
    private string _status = string.Empty;
    private string _detail = string.Empty;
    private string _timestamp = string.Empty;
    private double _progress;
    private bool _isActive;
    private bool _isHistory;
    private bool _isIndeterminate;
    private bool _showProgress;
    private bool _canCancel;
    private bool _showCancel;
    private bool _canViewDiagnostic;
    private bool _isLiveDiagnostic;
    private bool _isVerificationPending;
    private MutationVerificationPhase? _verificationPhase;
    private string _diagnosticProviderLabel = string.Empty;
    private string _diagnosticAutomationName = string.Empty;
    private string _diagnosticToolTip = string.Empty;

    public Guid OperationId { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public string Timestamp { get => _timestamp; set => SetProperty(ref _timestamp, value); }
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
    public bool IsHistory { get => _isHistory; set => SetProperty(ref _isHistory, value); }
    public bool IsIndeterminate { get => _isIndeterminate; set => SetProperty(ref _isIndeterminate, value); }
    public bool ShowProgress { get => _showProgress; set => SetProperty(ref _showProgress, value); }
    public bool CanCancel { get => _canCancel; set => SetProperty(ref _canCancel, value); }
    public bool ShowCancel { get => _showCancel; set => SetProperty(ref _showCancel, value); }
    public bool CanViewDiagnostic { get => _canViewDiagnostic; set => SetProperty(ref _canViewDiagnostic, value); }
    public bool IsLiveDiagnostic { get => _isLiveDiagnostic; set => SetProperty(ref _isLiveDiagnostic, value); }
    public bool IsVerificationPending { get => _isVerificationPending; set => SetProperty(ref _isVerificationPending, value); }
    public MutationVerificationPhase? VerificationPhase { get => _verificationPhase; set => SetProperty(ref _verificationPhase, value); }
    public string DiagnosticProviderLabel { get => _diagnosticProviderLabel; set => SetProperty(ref _diagnosticProviderLabel, value); }
    public string DiagnosticAutomationName { get => _diagnosticAutomationName; set => SetProperty(ref _diagnosticAutomationName, value); }
    public string DiagnosticToolTip { get => _diagnosticToolTip; set => SetProperty(ref _diagnosticToolTip, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class SourceHealthItem
{
    public string Name { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Status { get; set; } = "Unknown";
    public string Detail { get; set; } = string.Empty;
}

public sealed class SourceManagementListItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string Trust { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string LastUpdated { get; set; } = "Never";
    public string AgreementSummary { get; set; } = "No source agreements";
    public bool IsExplicit { get; set; }
    public bool CanRefresh { get; set; }
    public bool CanRemove { get; set; }
    public bool CanReset { get; set; }
    public bool CanEditExplicit { get; set; }
    public string ExplicitActionLabel => IsExplicit ? "Include in discovery" : "Make explicit";
}

public sealed class SearchRequestedEventArgs(string query) : EventArgs
{
    public string Query { get; } = query;
}

public sealed class PackageActionRequestedEventArgs(PackageListItem package) : EventArgs
{
    public PackageListItem Package { get; } = package;
}

public sealed class OperationCancelRequestedEventArgs(OperationListItem operation) : EventArgs
{
    public OperationListItem Operation { get; } = operation;
}

public sealed class SettingChangedEventArgs(string key, object value) : EventArgs
{
    public string Key { get; } = key;
    public object Value { get; } = value;
}

public sealed class SourceCommandRequestedEventArgs(SourceManagementListItem source) : EventArgs
{
    public SourceManagementListItem Source { get; } = source;
}

public sealed class BulkPackageActionRequestedEventArgs(IEnumerable<PackageListItem> packages) : EventArgs
{
    public IReadOnlyList<PackageListItem> Packages { get; } = new ReadOnlyCollection<PackageListItem>(packages.ToList());
}
