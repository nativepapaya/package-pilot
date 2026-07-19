using PackagePilot.Core.Models;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Parses the display-only icon metadata exposed by Add/Remove Programs. The result is a bounded
/// local resource reference; this service never executes or shell-opens the referenced file.
/// </summary>
internal static class WindowsDisplayIconReferenceParser
{
    internal const long MaxRasterBytes = 4L * 1024 * 1024;
    internal const long MaxExecutableResourceBytes = 1024L * 1024 * 1024;
    private const int MaximumResourceIndexMagnitude = 100_000;

    private static readonly HashSet<string> RasterExtensions = new(
        [".png", ".jpg", ".jpeg", ".webp", ".ico"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExecutableResourceExtensions = new(
        [".exe", ".dll"],
        StringComparer.OrdinalIgnoreCase);

    public static bool TryCreate(string? displayIcon, out AppIconReference? reference)
    {
        reference = null;
        if (!TryParse(displayIcon, out var path, out var resourceIndex)
            || !TryValidateExistingFile(path, out var fullPath, out var extension, out var length))
        {
            return false;
        }

        AppIconSourceKind kind;
        if (RasterExtensions.Contains(extension) && length <= MaxRasterBytes)
        {
            kind = AppIconSourceKind.ValidatedLocalResource;
        }
        else if (ExecutableResourceExtensions.Contains(extension)
                 && length <= MaxExecutableResourceBytes)
        {
            kind = AppIconSourceKind.ValidatedExecutableResource;
        }
        else
        {
            return false;
        }

        reference = new AppIconReference
        {
            Kind = kind,
            ResourcePath = fullPath,
            ResourceIndex = resourceIndex
        };
        return true;
    }

    internal static bool TryValidateExecutableReference(
        AppIconReference? reference,
        out string fullPath,
        out int resourceIndex,
        out long length,
        out DateTime lastWriteTimeUtc)
    {
        fullPath = string.Empty;
        resourceIndex = 0;
        length = 0;
        lastWriteTimeUtc = default;
        if (reference is not { Kind: AppIconSourceKind.ValidatedExecutableResource }
            || !TryValidateExistingFile(
                reference.ResourcePath,
                out fullPath,
                out var extension,
                out length)
            || !ExecutableResourceExtensions.Contains(extension)
            || length > MaxExecutableResourceBytes
            || reference.ResourceIndex is < -MaximumResourceIndexMagnitude
                or > MaximumResourceIndexMagnitude)
        {
            return false;
        }

        resourceIndex = reference.ResourceIndex ?? 0;
        try
        {
            lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
            return true;
        }
        catch (Exception exception) when (IsRecoverableFileFailure(exception))
        {
            return false;
        }
    }

    private static bool TryParse(string? value, out string path, out int resourceIndex)
    {
        path = string.Empty;
        resourceIndex = 0;
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf('\0') >= 0)
        {
            return false;
        }

        string suffix;
        if (text[0] == '"')
        {
            var closingQuote = text.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return false;
            }

            path = text[1..closingQuote];
            suffix = text[(closingQuote + 1)..].Trim();
        }
        else
        {
            var comma = text.LastIndexOf(',');
            if (comma >= 0 && int.TryParse(text[(comma + 1)..].Trim(), out _))
            {
                path = text[..comma].Trim();
                suffix = text[comma..];
            }
            else
            {
                path = text;
                suffix = string.Empty;
            }
        }

        if (!string.IsNullOrEmpty(suffix))
        {
            if (suffix[0] != ','
                || !int.TryParse(suffix[1..].Trim(), out resourceIndex)
                || resourceIndex is < -MaximumResourceIndexMagnitude
                    or > MaximumResourceIndexMagnitude)
            {
                return false;
            }
        }

        path = Environment.ExpandEnvironmentVariables(path.Trim());
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryValidateExistingFile(
        string? path,
        out string fullPath,
        out string extension,
        out long length)
    {
        fullPath = string.Empty;
        extension = string.Empty;
        length = 0;
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("::{", StringComparison.Ordinal)
            || path.StartsWith('@'))
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
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            extension = info.Extension;
            length = info.Length;
            return length > 0;
        }
        catch (Exception exception) when (IsRecoverableFileFailure(exception))
        {
            return false;
        }
    }

    private static bool IsRecoverableFileFailure(Exception exception) => exception is
        ArgumentException or
        NotSupportedException or
        PathTooLongException or
        IOException or
        UnauthorizedAccessException;
}
