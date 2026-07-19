using PackagePilot.Core.Models;

namespace PackagePilot.App.Services;

internal static class AppIconReferencePolicy
{
    internal const long MaxLocalIconBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(
        [".png", ".jpg", ".jpeg", ".webp", ".ico"],
        StringComparer.OrdinalIgnoreCase);

    public static bool TryCreateValidatedLocal(
        string? path,
        long? fileLength,
        int? resourceIndex,
        out AppIconReference? reference)
    {
        reference = null;
        if (!TryNormalizeLocalPath(path, fileLength, out var fullPath))
        {
            return false;
        }

        reference = new AppIconReference
        {
            Kind = AppIconSourceKind.ValidatedLocalResource,
            ResourcePath = fullPath,
            ResourceIndex = resourceIndex
        };
        return true;
    }

    public static bool TryNormalizeLocalPath(
        string? path,
        long? fileLength,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)
            || fileLength is < 0 or > MaxLocalIconBytes
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("::{", StringComparison.Ordinal)
            || Uri.TryCreate(path, UriKind.Absolute, out var uri)
                && !uri.IsFile)
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }

            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(fullPath)))
        {
            return false;
        }

        return true;
    }
}
