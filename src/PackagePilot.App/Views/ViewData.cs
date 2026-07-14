using System.Collections.ObjectModel;

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

public sealed class BulkPackageActionRequestedEventArgs(IEnumerable<PackageListItem> packages) : EventArgs
{
    public IReadOnlyList<PackageListItem> Packages { get; } = new ReadOnlyCollection<PackageListItem>(packages.ToList());
}
