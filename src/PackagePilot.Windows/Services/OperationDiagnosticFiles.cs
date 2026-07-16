using System.Text;
using System.Text.RegularExpressions;

namespace PackagePilot.Windows.Services;

internal static class OperationDiagnosticFiles
{
    internal const int MaximumDisplayedBytesPerLog = 512 * 1024;
    private const int MaximumHeaderBytes = 128 * 1024;
    internal const int MaximumCorrelationCandidates = 32;
    internal const int MaximumEnumeratedLogFiles = 512;
    internal const int MaximumOwnedLogCount = 100;
    internal const long MaximumIndividualOwnedLogBytes = 16L * 1024 * 1024;
    private const long MaximumOwnedLogBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan MaximumOwnedLogAge = TimeSpan.FromDays(30);

    public static bool TryPrepareInstallerLog(
        string trustedRoot,
        string root,
        Guid operationId,
        out string logPath)
    {
        logPath = GetInstallerLogPath(root, operationId);
        try
        {
            if (!TryEnsureSafeOwnedRoot(trustedRoot, root, create: true)
                || (File.Exists(logPath) && IsReparsePoint(logPath)))
            {
                return false;
            }

            VerifyWriteAccess(root);
            PruneOwnedLogs(root, operationId);
            return true;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return false;
        }
    }

    public static string GetInstallerLogPath(string root, Guid operationId) =>
        Path.Combine(root, $"{operationId:N}.log");

    public static async Task<BoundedLogText?> ReadInstallerLogAsync(
        string trustedRoot,
        string root,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(root)
                || !TryEnsureSafeOwnedRoot(trustedRoot, root, create: false))
            {
                return null;
            }
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }

        var path = GetInstallerLogPath(root, operationId);
        return await ReadBoundedAsync(
            path,
            MaximumDisplayedBytesPerLog,
            tail: true,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CorrelatedLog?> FindWingetComLogAsync(
        string diagnosticsRoot,
        Guid correlationId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(diagnosticsRoot) || IsReparsePoint(diagnosticsRoot))
            {
                return null;
            }
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }

        IReadOnlyList<FileInfo> candidates;
        try
        {
            candidates = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var referenceStart = startedAt == default
                    ? completedAt == default ? DateTimeOffset.UtcNow : completedAt
                    : startedAt;
                var referenceEnd = completedAt == default ? DateTimeOffset.UtcNow : completedAt;
                var earliest = AddHoursClamped(referenceStart.UtcDateTime, -1);
                var latest = AddHoursClamped(referenceEnd.UtcDateTime, 1);

                var files = Directory.EnumerateFiles(
                        diagnosticsRoot,
                        "WinGetCOM-*.log",
                        SearchOption.TopDirectoryOnly)
                    .Take(MaximumEnumeratedLogFiles)
                    .Select(path => new FileInfo(path))
                    .Where(file => file.Exists
                        && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToArray();

                var inWindow = files
                    .Where(file => file.LastWriteTimeUtc >= earliest
                        && file.LastWriteTimeUtc <= latest)
                    .Take(MaximumCorrelationCandidates)
                    .ToArray();
                return (IReadOnlyList<FileInfo>)(inWindow.Length > 0
                    ? inWindow
                    : files.Take(MaximumCorrelationCandidates).ToArray());
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }

        var correlationText = correlationId.ToString("D");
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var log = await ReadCorrelatedWingetLogAsync(
                candidate.FullName,
                correlationText,
                cancellationToken).ConfigureAwait(false);
            if (log is not null)
            {
                return new CorrelatedLog(candidate.Name, log);
            }
        }

        return null;
    }

    public static Task DeleteOwnedLogsAsync(
        string trustedRoot,
        string root,
        IEnumerable<Guid> operationIds,
        CancellationToken cancellationToken)
    {
        var exactOperationIds = operationIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        return Task.Run(() =>
        {
            var hadFailure = false;
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }

                if (!TryEnsureSafeOwnedRoot(trustedRoot, root, create: false))
                {
                    throw new IOException("The app-owned installer log folder is not safe to modify.");
                }
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                throw new IOException("The app-owned installer log folder could not be verified.", exception);
            }

            foreach (var operationId in exactOperationIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = GetInstallerLogPath(root, operationId);
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    if (IsReparsePoint(path))
                    {
                        hadFailure = true;
                        continue;
                    }

                    File.Delete(path);
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    hadFailure = true;
                }
            }

            if (hadFailure)
            {
                throw new IOException("One or more app-owned installer logs could not be removed.");
            }
        }, cancellationToken);
    }

    public static Task FinalizeInstallerLogAsync(
        string trustedRoot,
        string root,
        Guid operationId)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(root)
                    || !TryEnsureSafeOwnedRoot(trustedRoot, root, create: false))
                {
                    return;
                }

                var path = GetInstallerLogPath(root, operationId);
                if (File.Exists(path)
                    && !IsReparsePoint(path))
                {
                    var file = new FileInfo(path);
                    if (file.Length > MaximumIndividualOwnedLogBytes)
                    {
                        File.Delete(path);
                        File.WriteAllText(
                            path,
                            "[Installer log removed because it exceeded Package Pilot's 16 MB safety limit.]",
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }
                }
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                // Finalization is best-effort and must never change the package result.
            }

            PruneOwnedLogs(root, Guid.Empty);
        });
    }

    private static async Task<BoundedLogText?> ReadBoundedAsync(
        string path,
        int maximumBytes,
        bool tail,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            var encoding = await DetectEncodingAsync(stream, cancellationToken).ConfigureAwait(false);
            return await ReadBoundedFromStreamAsync(
                stream,
                encoding,
                maximumBytes,
                tail,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }
    }

    private static async Task<BoundedLogText?> ReadCorrelatedWingetLogAsync(
        string path,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            var encoding = await DetectEncodingAsync(stream, cancellationToken).ConfigureAwait(false);
            var header = await ReadBoundedFromStreamAsync(
                stream,
                encoding,
                MaximumHeaderBytes,
                tail: false,
                cancellationToken).ConfigureAwait(false);
            if (!ContainsOperationCorrelation(header.Text, correlationId))
            {
                return null;
            }

            return await ReadBoundedFromStreamAsync(
                stream,
                encoding,
                MaximumDisplayedBytesPerLog,
                tail: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return null;
        }
    }

    private static async Task<BoundedLogText> ReadBoundedFromStreamAsync(
        FileStream stream,
        Encoding encoding,
        int maximumBytes,
        bool tail,
        CancellationToken cancellationToken)
    {
        var length = stream.Length;
        var truncated = length > maximumBytes;
        var start = tail && truncated ? length - maximumBytes : 0;
        if (encoding is UnicodeEncoding && start % 2 != 0)
        {
            start++;
        }

        stream.Seek(start, SeekOrigin.Begin);
        var available = Math.Max(0, stream.Length - start);
        var bytesToRead = (int)Math.Min(maximumBytes, available);
        var buffer = new byte[bytesToRead];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(
                buffer.AsMemory(read, buffer.Length - read),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        var text = encoding.GetString(buffer, 0, read).TrimStart('\uFEFF');
        if (tail && truncated)
        {
            var firstLineBreak = text.IndexOf('\n');
            if (firstLineBreak >= 0 && firstLineBreak + 1 < text.Length)
            {
                text = text[(firstLineBreak + 1)..];
            }
        }

        return new BoundedLogText(SanitizePlainText(text), truncated);
    }

    private static async Task<Encoding> DetectEncodingAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        stream.Seek(0, SeekOrigin.Begin);
        var read = await stream.ReadAsync(prefix.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (read >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (read >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        if (read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    }

    private static bool ContainsOperationCorrelation(string text, string correlationId)
    {
        var cursor = 0;
        const string key = "operationId";
        while ((cursor = text.IndexOf(key, cursor, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var valueStart = cursor + key.Length;
            var valueIndex = text.IndexOf(
                correlationId,
                valueStart,
                StringComparison.OrdinalIgnoreCase);
            if (valueIndex >= valueStart
                && valueIndex - valueStart <= 96
                && IsCorrelationSeparator(text.AsSpan(valueStart, valueIndex - valueStart)))
            {
                return true;
            }

            cursor = valueStart;
        }

        return false;
    }

    private static bool IsCorrelationSeparator(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '\r' or '\n')
            {
                return false;
            }
        }

        return true;
    }

    private static DateTime AddHoursClamped(DateTime value, int hours)
    {
        var delta = TimeSpan.FromHours(hours);
        if (hours < 0 && value - DateTime.MinValue < delta.Negate())
        {
            return DateTime.MinValue;
        }

        if (hours > 0 && DateTime.MaxValue - value < delta)
        {
            return DateTime.MaxValue;
        }

        return value + delta;
    }

    private static string SanitizePlainText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static void PruneOwnedLogs(string root, Guid currentOperationId)
    {
        try
        {
            if (!Directory.Exists(root) || IsReparsePoint(root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - MaximumOwnedLogAge;
            var files = Directory.EnumerateFiles(root, "*.log", SearchOption.TopDirectoryOnly)
                .Take(MaximumEnumeratedLogFiles)
                .Select(path => new FileInfo(path))
                .Where(file => Guid.TryParseExact(
                        Path.GetFileNameWithoutExtension(file.Name),
                        "N",
                        out _)
                    && (file.Attributes & FileAttributes.ReparsePoint) == 0
                    && !string.Equals(
                        Path.GetFileNameWithoutExtension(file.Name),
                        currentOperationId.ToString("N"),
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            long retainedBytes = 0;
            var maximumRetained = currentOperationId == Guid.Empty
                ? MaximumOwnedLogCount
                : MaximumOwnedLogCount - 1;
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                var retain = index < maximumRetained
                    && file.LastWriteTimeUtc >= cutoff
                    && retainedBytes + file.Length <= MaximumOwnedLogBytes;
                if (retain)
                {
                    retainedBytes += file.Length;
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch (Exception exception) when (IsExpectedFileException(exception))
                {
                    // Retention is best-effort and must never block a package operation.
                }
            }
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            // Retention is best-effort and must never block a package operation.
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool TryEnsureSafeOwnedRoot(
        string trustedRoot,
        string root,
        bool create)
    {
        var anchor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(anchor, candidate);
        if (relative is "." or ".."
            || Path.IsPathRooted(relative)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || !Directory.Exists(anchor)
            || IsReparsePoint(anchor))
        {
            return false;
        }

        var current = anchor;
        foreach (var component in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (Directory.Exists(current))
            {
                if (IsReparsePoint(current))
                {
                    return false;
                }

                continue;
            }

            if (!create || File.Exists(current))
            {
                return false;
            }

            Directory.CreateDirectory(current);
            if (IsReparsePoint(current))
            {
                return false;
            }
        }

        return Directory.Exists(candidate) && !IsReparsePoint(candidate);
    }

    private static void VerifyWriteAccess(string root)
    {
        var probePath = Path.Combine(root, $".write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }
        }
    }

    private static bool IsExpectedFileException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;
}

internal sealed record BoundedLogText(string Text, bool IsTruncated);

internal sealed record CorrelatedLog(string FileName, BoundedLogText Content);

internal static partial class OperationDiagnosticRedactor
{
    [GeneratedRegex(
        @"(?im)^(\s*(?:Cookie|Set-Cookie)\s*:\s*).*$",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex CookieHeaderRegex();

    [GeneratedRegex(
        @"(?im)([""']?\b(?:authorization|proxy-authorization|cookie|set-cookie|password|passwd|token|access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|api[_-]?key|sig|signature)\b[""']?\s*[:=]\s*[""']?)([^\r\n;""'}]+)",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex AssignmentSecretRegex();

    [GeneratedRegex(
        @"(?i)([?&](?:password|passwd|token|access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|api[_-]?key|sig|signature)=)[^&#\s]+",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(
        @"(?i)(\b(?:Bearer|Basic)\s+)[A-Za-z0-9+/=._~-]+",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex AuthorizationValueRegex();

    [GeneratedRegex(
        @"(?is)(<Data\b[^>]*\bName\s*=\s*[""'](?:authorization|password|passwd|token|access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|api[_-]?key|sig|signature)[""'][^>]*>)(.*?)(</Data>)",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex EventDataSecretRegex();

    [GeneratedRegex(
        @"(?i)(\B(?:--?|/)(?:password|passwd|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key)\s+)([^\s""']+)",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex CommandLineSecretRegex();

    [GeneratedRegex(
        @"(?i)(\b[a-z][a-z0-9+.-]*://)([^/\s:@]+):([^@/\s]+)@",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex UriUserInfoRegex();

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string redacted;
        try
        {
            redacted = CookieHeaderRegex().Replace(text, "$1[REDACTED]");
            redacted = AssignmentSecretRegex().Replace(redacted, "$1[REDACTED]");
            redacted = QuerySecretRegex().Replace(redacted, "$1[REDACTED]");
            redacted = AuthorizationValueRegex().Replace(redacted, "$1[REDACTED]");
            redacted = EventDataSecretRegex().Replace(redacted, "$1[REDACTED]$3");
            redacted = CommandLineSecretRegex().Replace(redacted, "$1[REDACTED]");
            redacted = UriUserInfoRegex().Replace(redacted, "$1[REDACTED]@");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[Log content was withheld because the safety redaction limit was reached.]";
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            redacted = redacted.Replace(
                profile,
                "%USERPROFILE%",
                StringComparison.OrdinalIgnoreCase);
        }

        return redacted;
    }
}
