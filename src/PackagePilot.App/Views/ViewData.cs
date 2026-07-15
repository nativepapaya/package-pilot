using System.Collections.ObjectModel;
using PackagePilot.Core.Models;

namespace PackagePilot.App.Views;

public sealed class PackageListItem
{
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string AvailableVersion { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ActionLabel { get; set; } = "Install";
    public string IconGlyph { get; set; } = "\uE896";
    public Uri? IconUri { get; set; }
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
    public bool IsActionEnabled { get; set; } = true;
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
}

internal static class PackageListItemComparer
{
    public static bool HaveSameRows(
        IReadOnlyList<PackageListItem> current,
        IReadOnlyList<PackageListItem> replacement)
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
                || !string.Equals(left.Status, right.Status, StringComparison.Ordinal)
                || !string.Equals(left.ActionLabel, right.ActionLabel, StringComparison.Ordinal)
                || !string.Equals(left.IconGlyph, right.IconGlyph, StringComparison.Ordinal)
                || left.RequiresElevation != right.RequiresElevation
                || left.IsActionEnabled != right.IsActionEnabled
                || left.InstalledActionKind != right.InstalledActionKind
                || left.WingetPackage != right.WingetPackage
                || !string.Equals(left.InstalledAppId, right.InstalledAppId, StringComparison.Ordinal)
                || !string.Equals(left.PackageFullName, right.PackageFullName, StringComparison.Ordinal)
                || left.ActionDestination != right.ActionDestination
                || left.IconUri != right.IconUri)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class OperationListItem
{
    public Guid OperationId { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public double Progress { get; set; }
    public bool IsActive { get; set; }
    public bool IsIndeterminate { get; set; }
    public bool CanCancel { get; set; }
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
