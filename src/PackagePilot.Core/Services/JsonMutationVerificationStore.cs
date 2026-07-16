using System.Text.Json;
using System.Text.Json.Serialization;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

public interface IMutationVerificationStore
{
    MutationVerificationSnapshot? Load();

    /// <summary>
    /// Atomically replaces the durable snapshot. If this method throws, the previous
    /// snapshot must remain authoritative.
    /// </summary>
    void Save(MutationVerificationSnapshot snapshot);
}

public sealed record MutationVerificationSnapshot
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumMarkerCount = 256;
    public const int MaximumPackageIdLength = 512;
    public const int MaximumSourceIdLength = 256;
    public const int MaximumDisplayTextLength = 1024;
    public const int MaximumBootSessionIdLength = 128;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonRequired]
    public IReadOnlyList<MutationVerificationMarker> Markers { get; init; } = null!;

    [JsonRequired]
    public IReadOnlyList<Guid> VerifiedOperationIds { get; init; } = null!;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion
            || Markers is null
            || VerifiedOperationIds is null)
        {
            throw new InvalidDataException(
                "The mutation verification recovery file has an unsupported or incomplete schema.");
        }

        if (Markers.Count > MaximumMarkerCount)
        {
            throw new InvalidDataException(
                "The mutation verification recovery file contains too many pending operations.");
        }

        if (VerifiedOperationIds.Count > MutationVerificationTracker.MaximumVerifiedOperationCount
            || VerifiedOperationIds.Any(operationId => operationId == Guid.Empty)
            || VerifiedOperationIds.Distinct().Count() != VerifiedOperationIds.Count)
        {
            throw new InvalidDataException(
                "The mutation verification recovery file contains invalid verified-operation identifiers.");
        }

        var packageKeys = new HashSet<PackageKey>();
        var markerOperationIds = new HashSet<Guid>();
        var markerRevisionIds = new HashSet<Guid>();
        var verifiedOperationIds = VerifiedOperationIds.ToHashSet();
        foreach (var marker in Markers)
        {
            if (marker is null)
            {
                throw new InvalidDataException(
                    "The mutation verification recovery file contains a null marker.");
            }

            ValidateMarker(marker);
            if (!packageKeys.Add(marker.Package.Key))
            {
                throw new InvalidDataException(
                    "The mutation verification recovery file contains duplicate package targets.");
            }

            if (!markerOperationIds.Add(marker.OperationId)
                || !markerRevisionIds.Add(marker.RevisionId)
                || verifiedOperationIds.Contains(marker.OperationId))
            {
                throw new InvalidDataException(
                    "The mutation verification recovery file contains conflicting operation identifiers.");
            }
        }
    }

    private static void ValidateMarker(MutationVerificationMarker marker)
    {
        if (marker.OperationId == Guid.Empty
            || marker.RevisionId == Guid.Empty
            || marker.RecordedAt == default
            || !Enum.IsDefined(marker.Kind)
            || !Enum.IsDefined(marker.Phase))
        {
            throw new InvalidDataException(
                "The mutation verification recovery file contains invalid operation metadata.");
        }

        if (marker.Package is null
            || marker.Package.Key is null
            || string.IsNullOrWhiteSpace(marker.Package.Key.Id)
            || marker.Package.Key.Id.Length > MaximumPackageIdLength
            || string.IsNullOrWhiteSpace(marker.Package.Key.SourceId)
            || marker.Package.Key.SourceId.Length > MaximumSourceIdLength)
        {
            throw new InvalidDataException(
                "The mutation verification recovery file contains an invalid exact package target.");
        }

        if (string.IsNullOrWhiteSpace(marker.BootSessionId)
            || marker.BootSessionId.Length > MaximumBootSessionIdLength
            || IsMissingOrOversized(marker.Package.Name)
            || IsMissingOrOversized(marker.Package.Publisher)
            || IsMissingOrOversized(marker.Package.SourceName)
            || IsMissingOrOversized(marker.Package.Description)
            || (marker.Package.InstalledVersion?.Length ?? 0) > MaximumDisplayTextLength
            || (marker.Package.AvailableVersion?.Length ?? 0) > MaximumDisplayTextLength)
        {
            throw new InvalidDataException(
                "The mutation verification recovery file contains invalid or oversized marker data.");
        }
    }

    private static bool IsMissingOrOversized(string? value) =>
        value is null || value.Length > MaximumDisplayTextLength;
}

/// <summary>Stores mutation verification markers with an atomic same-directory replacement.</summary>
public sealed class JsonMutationVerificationStore : IMutationVerificationStore
{
    private const long MaximumSnapshotBytes = 1024 * 1024;
    private readonly string _filePath;
    private readonly JsonSerializerOptions _options;

    public JsonMutationVerificationStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        _options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    }

    public MutationVerificationSnapshot? Load()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        using (stream)
        {
            if (stream.Length > MaximumSnapshotBytes)
            {
                throw new InvalidDataException(
                    "The mutation verification recovery file is unexpectedly large.");
            }

            try
            {
                var snapshot = JsonSerializer.Deserialize<MutationVerificationSnapshot>(
                    stream,
                    _options);
                if (snapshot is null)
                {
                    throw new InvalidDataException(
                        "The mutation verification recovery file is empty.");
                }

                snapshot.Validate();
                return snapshot;
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw new InvalidDataException(
                    "The mutation verification recovery file is malformed.",
                    exception);
            }
        }
    }

    public void Save(MutationVerificationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();

        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot, _options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
