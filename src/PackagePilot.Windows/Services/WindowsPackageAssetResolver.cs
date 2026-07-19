using PackagePilot.Core.Models;

namespace PackagePilot.Windows.Services;

internal static class WindowsPackageAssetResolver
{
    private const long MaxAssetBytes = 4L * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(
        [".png", ".jpg", ".jpeg", ".webp", ".ico"],
        StringComparer.OrdinalIgnoreCase);

    public static bool TryResolve(
        string? packageRoot,
        string? logicalAssetPath,
        out AppIconReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(packageRoot)
            || string.IsNullOrWhiteSpace(logicalAssetPath))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(packageRoot)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var relative = Uri.UnescapeDataString(logicalAssetPath)
                .TrimStart('/', '\\')
                .Replace('/', Path.DirectorySeparatorChar);
            var requested = Path.GetFullPath(Path.Combine(root, relative));
            if (!requested.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !AllowedExtensions.Contains(Path.GetExtension(requested)))
            {
                return false;
            }

            var resolved = ResolveQualifiedAsset(requested);
            if (resolved is null)
            {
                return false;
            }

            var info = new FileInfo(resolved);
            if (!info.Exists
                || info.Length is <= 0 or > MaxAssetBytes
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            reference = new AppIconReference
            {
                Kind = AppIconSourceKind.MsixPackageAsset,
                ResourcePath = info.FullName
            };
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            IOException or
            UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? ResolveQualifiedAsset(string requested)
    {
        if (File.Exists(requested))
        {
            return requested;
        }

        var directory = Path.GetDirectoryName(requested);
        var extension = Path.GetExtension(requested);
        var baseName = Path.GetFileNameWithoutExtension(requested);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, $"{baseName}.*{extension}", SearchOption.TopDirectoryOnly)
            .Where(candidate => AllowedExtensions.Contains(Path.GetExtension(candidate)))
            .Select(candidate => (candidate, score: ScoreCandidate(Path.GetFileName(candidate), baseName)))
            .Where(candidate => candidate.score < int.MaxValue)
            .OrderBy(candidate => candidate.score)
            .ThenBy(candidate => candidate.candidate, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.candidate)
            .FirstOrDefault();
    }

    private static int ScoreCandidate(string fileName, string baseName)
    {
        var qualifier = Path.GetFileNameWithoutExtension(fileName)[baseName.Length..];
        if (qualifier.Equals(".targetsize-64", StringComparison.OrdinalIgnoreCase)) return 0;
        if (qualifier.Equals(".targetsize-48", StringComparison.OrdinalIgnoreCase)) return 1;
        if (qualifier.Equals(".targetsize-72", StringComparison.OrdinalIgnoreCase)) return 2;
        if (qualifier.Equals(".targetsize-96", StringComparison.OrdinalIgnoreCase)) return 3;
        if (qualifier.Equals(".targetsize-44", StringComparison.OrdinalIgnoreCase)) return 4;
        if (qualifier.Equals(".targetsize-256", StringComparison.OrdinalIgnoreCase)) return 5;
        if (qualifier.Equals(".scale-100", StringComparison.OrdinalIgnoreCase)) return 10;
        if (qualifier.Equals(".scale-125", StringComparison.OrdinalIgnoreCase)) return 11;
        if (qualifier.Equals(".scale-150", StringComparison.OrdinalIgnoreCase)) return 12;
        if (qualifier.Equals(".scale-200", StringComparison.OrdinalIgnoreCase)) return 13;
        if (qualifier.Equals(".scale-400", StringComparison.OrdinalIgnoreCase)) return 14;
        if (qualifier.Contains("_altform-unplated", StringComparison.OrdinalIgnoreCase)) return 20;
        return int.MaxValue;
    }
}
