namespace PackagePilot.Core.Models;

/// <summary>The outcome of looking for and importing the neutral identity handoff.</summary>
public enum IdentityMigrationImportOutcome
{
    NoMigrationAvailable,
    Imported,
    AlreadyImported,
    SourceIdentityMatchesCurrent
}

public sealed record IdentityMigrationExportResult
{
    public required string FilePath { get; init; }
    public int SettingCount { get; init; }
    public int OperationHistoryCount { get; init; }
}

public sealed record IdentityMigrationImportResult
{
    public IdentityMigrationImportOutcome Outcome { get; init; }
    public int SettingCount { get; init; }
    public int OperationHistoryCount { get; init; }
    public bool RecoveredInterruptedExport { get; init; }
}

/// <summary>Stable location shared by the retiring and replacement package identities.</summary>
public static class IdentityMigrationPaths
{
    public const string FileName = "package-pilot-identity-migration.json";

    public static string GetNeutralFilePath(string? localApplicationDataPath = null)
    {
        var root = string.IsNullOrWhiteSpace(localApplicationDataPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localApplicationDataPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The non-package LocalAppData path is unavailable.");
        }

        return Path.Combine(
            Path.GetFullPath(root),
            "Package Pilot",
            "Identity Migration",
            FileName);
    }
}
