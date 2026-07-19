using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Matches exact WinGet package identifiers when the installed catalog cannot retain the
/// originating remote source. This is deliberately directional: only the installed side may
/// use a local-catalog sentinel, and callers must handle remote-source ambiguity separately.
/// </summary>
public static class WingetPackageIdentity
{
    public const string PredefinedInstalledSourceId = "*PredefinedInstalledSource";

    public static bool IsUnattributedInstalledSource(string? sourceId) =>
        string.IsNullOrWhiteSpace(sourceId)
        || string.Equals(sourceId, "installed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(sourceId, "unknown", StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            sourceId,
            PredefinedInstalledSourceId,
            StringComparison.OrdinalIgnoreCase);

    public static bool MatchesInstalledPackage(PackageKey requested, PackageKey installed)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(installed);

        return string.Equals(requested.Id, installed.Id, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(
                    requested.SourceId,
                    installed.SourceId,
                    StringComparison.OrdinalIgnoreCase)
                || IsUnattributedInstalledSource(installed.SourceId));
    }
}
