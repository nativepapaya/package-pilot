using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Moves user preferences and retained operation history across a deliberate MSIX
/// identity change through a neutral, non-package LocalAppData handoff file.
/// </summary>
public sealed class IdentityMigrationService
{
    /// <summary>
    /// Reserved target-setting receipt. It contains only a schema/source hash and is
    /// deliberately excluded from future settings exports.
    /// </summary>
    public const string CompletionMarkerSettingKey = "PackagePilot.Internal.IdentityMigrationCompleted";

    private const int CurrentSchemaVersion = 1;
    private const string ProductMarker = "PackagePilot";
    private const long MaximumMigrationBytes = 16 * 1024 * 1024;
    private const long MaximumHistoryBytes = 12 * 1024 * 1024;
    private const int MaximumSettingCount = 2_048;
    private const int MaximumSettingKeyLength = 512;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private readonly string _migrationFilePath;
    private readonly string _operationHistoryFilePath;
    private readonly string _currentPackageIdentity;
    private readonly IIdentityMigrationSettingsStore _settingsStore;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    public IdentityMigrationService(
        string migrationFilePath,
        string currentPackageIdentity,
        IIdentityMigrationSettingsStore settingsStore,
        string operationHistoryFilePath,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPackageIdentity);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationHistoryFilePath);

        _migrationFilePath = Path.GetFullPath(migrationFilePath);
        _operationHistoryFilePath = Path.GetFullPath(operationHistoryFilePath);
        if (string.Equals(
                _migrationFilePath,
                _operationHistoryFilePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The migration and operation-history paths must be different.",
                nameof(operationHistoryFilePath));
        }

        _currentPackageIdentity = currentPackageIdentity.Trim();
        _settingsStore = settingsStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public string MigrationFilePath => _migrationFilePath;

    /// <summary>
    /// Writes or replaces the neutral handoff. This is called by the final build
    /// carrying the retiring development identity.
    /// </summary>
    public async Task<IdentityMigrationExportResult> ExportAsync(
        CancellationToken cancellationToken = default)
    {
        await using var migrationLock = await AcquireLockAsync(cancellationToken)
            .ConfigureAwait(false);
        var settings = await _settingsStore.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(settings);
        var exportableSettings = settings
            .Where(pair => !string.Equals(
                pair.Key,
                CompletionMarkerSettingKey,
                StringComparison.Ordinal))
            .ToArray();
        if (exportableSettings.Length > MaximumSettingCount)
        {
            throw new InvalidDataException("The Package Pilot settings handoff is too large.");
        }

        var encodedSettings = exportableSettings
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(EncodeSetting)
            .ToArray();
        var history = await ReadOperationHistoryAsync(cancellationToken).ConfigureAwait(false);
        var envelope = new MigrationEnvelope
        {
            SchemaVersion = CurrentSchemaVersion,
            Product = ProductMarker,
            SourcePackageIdentity = _currentPackageIdentity,
            ExportedAtUtc = _timeProvider.GetUtcNow(),
            Settings = encodedSettings,
            OperationHistory = history
        };

        await WriteJsonAtomicallyAsync(
                _migrationFilePath,
                envelope,
                MaximumMigrationBytes,
                cancellationToken)
            .ConfigureAwait(false);

        return new IdentityMigrationExportResult
        {
            FilePath = _migrationFilePath,
            SettingCount = encodedSettings.Length,
            OperationHistoryCount = history.GetArrayLength()
        };
    }

    /// <summary>
    /// Imports the handoff only when it came from a different package identity.
    /// The handoff is removed only after both settings and history are durable.
    /// </summary>
    public async Task<IdentityMigrationImportResult> ImportOnceAsync(
        CancellationToken cancellationToken = default)
    {
        await using var migrationLock = await AcquireLockAsync(cancellationToken)
            .ConfigureAwait(false);
        var recovered = await RecoverInterruptedExportAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!File.Exists(_migrationFilePath))
        {
            return new IdentityMigrationImportResult
            {
                Outcome = IdentityMigrationImportOutcome.NoMigrationAvailable,
                RecoveredInterruptedExport = recovered
            };
        }

        var envelope = await ReadAndValidateEnvelopeAsync(
                _migrationFilePath,
                cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(
                envelope.SourcePackageIdentity,
                _currentPackageIdentity,
                StringComparison.Ordinal))
        {
            return new IdentityMigrationImportResult
            {
                Outcome = IdentityMigrationImportOutcome.SourceIdentityMatchesCurrent,
                SettingCount = envelope.Settings.Count,
                OperationHistoryCount = envelope.OperationHistory.GetArrayLength(),
                RecoveredInterruptedExport = recovered
            };
        }

        var decodedSettings = DecodeSettings(envelope.Settings);
        var completionMarker = CreateCompletionMarker(envelope);
        var targetSettings = await _settingsStore.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false);
        if (targetSettings.TryGetValue(CompletionMarkerSettingKey, out var existingMarker)
            && existingMarker is string marker
            && string.Equals(marker, completionMarker, StringComparison.Ordinal))
        {
            File.Delete(_migrationFilePath);
            DeleteInterruptedExportFiles();
            return new IdentityMigrationImportResult
            {
                Outcome = IdentityMigrationImportOutcome.AlreadyImported,
                SettingCount = decodedSettings.Count,
                OperationHistoryCount = envelope.OperationHistory.GetArrayLength(),
                RecoveredInterruptedExport = recovered
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _settingsStore.ApplyAsync(decodedSettings, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAtomicallyAsync(
                _operationHistoryFilePath,
                envelope.OperationHistory,
                MaximumHistoryBytes,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await _settingsStore.ApplyAsync(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [CompletionMarkerSettingKey] = completionMarker
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Once the receipt is durable, finish cleanup even if cancellation arrives.
        // A failed delete is safe: the next launch observes the receipt and only cleans up.
        File.Delete(_migrationFilePath);
        DeleteInterruptedExportFiles();

        return new IdentityMigrationImportResult
        {
            Outcome = IdentityMigrationImportOutcome.Imported,
            SettingCount = decodedSettings.Count,
            OperationHistoryCount = envelope.OperationHistory.GetArrayLength(),
            RecoveredInterruptedExport = recovered
        };
    }

    private async Task<JsonElement> ReadOperationHistoryAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_operationHistoryFilePath))
        {
            return JsonSerializer.SerializeToElement(Array.Empty<object>(), _jsonOptions);
        }

        var info = new FileInfo(_operationHistoryFilePath);
        if (info.Length > MaximumHistoryBytes)
        {
            throw new InvalidDataException("The Package Pilot operation history is too large to migrate.");
        }

        try
        {
            await using var stream = new FileStream(
                _operationHistoryFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 64
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "The Package Pilot operation history must be a JSON array.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Package Pilot operation history is invalid.",
                exception);
        }
    }

    private async Task<MigrationEnvelope> ReadAndValidateEnvelopeAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(filePath);
        if (info.Length > MaximumMigrationBytes)
        {
            throw new InvalidDataException("The Package Pilot identity migration file is too large.");
        }

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var envelope = await JsonSerializer.DeserializeAsync<MigrationEnvelope>(
                    stream,
                    _jsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The Package Pilot identity migration file is empty.");
            ValidateEnvelope(envelope);
            return envelope;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Package Pilot identity migration file is invalid.",
                exception);
        }
    }

    private static void ValidateEnvelope(MigrationEnvelope envelope)
    {
        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Package Pilot identity migration schema version {envelope.SchemaVersion}.");
        }

        if (!string.Equals(envelope.Product, ProductMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The identity migration file is for a different product.");
        }

        if (string.IsNullOrWhiteSpace(envelope.SourcePackageIdentity)
            || envelope.SourcePackageIdentity.Length > 1_024)
        {
            throw new InvalidDataException("The source package identity is invalid.");
        }

        if (envelope.ExportedAtUtc == default)
        {
            throw new InvalidDataException("The identity migration export timestamp is missing.");
        }

        if (envelope.Settings is null || envelope.Settings.Count > MaximumSettingCount)
        {
            throw new InvalidDataException("The Package Pilot settings handoff is invalid.");
        }

        if (envelope.OperationHistory.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The migrated operation history must be a JSON array.");
        }

        // Decode performs strict value-shape and duplicate-key validation before any import writes.
        _ = DecodeSettings(envelope.Settings);
    }

    private MigrationSettingEntry EncodeSetting(KeyValuePair<string, object> setting)
    {
        ValidateSettingKey(setting.Key);
        ArgumentNullException.ThrowIfNull(setting.Value);
        return setting.Value switch
        {
            string value => CreateSetting(setting.Key, MigrationValueKind.String, value),
            bool value => CreateSetting(setting.Key, MigrationValueKind.Boolean, value),
            byte value => CreateSetting(setting.Key, MigrationValueKind.Byte, value),
            sbyte value => CreateSetting(setting.Key, MigrationValueKind.SByte, value),
            short value => CreateSetting(setting.Key, MigrationValueKind.Int16, value),
            ushort value => CreateSetting(setting.Key, MigrationValueKind.UInt16, value),
            int value => CreateSetting(setting.Key, MigrationValueKind.Int32, value),
            uint value => CreateSetting(setting.Key, MigrationValueKind.UInt32, value),
            long value => CreateSetting(setting.Key, MigrationValueKind.Int64, value),
            ulong value => CreateSetting(setting.Key, MigrationValueKind.UInt64, value),
            float value => CreateSetting(setting.Key, MigrationValueKind.Single, value),
            double value => CreateSetting(setting.Key, MigrationValueKind.Double, value),
            char value => CreateSetting(setting.Key, MigrationValueKind.Char, value.ToString()),
            Guid value => CreateSetting(setting.Key, MigrationValueKind.Guid, value),
            DateTimeOffset value => CreateSetting(setting.Key, MigrationValueKind.DateTimeOffset, value),
            TimeSpan value => CreateSetting(setting.Key, MigrationValueKind.TimeSpan, value),
            _ => throw new InvalidDataException(
                $"Setting '{setting.Key}' uses an unsupported application-setting type.")
        };
    }

    private MigrationSettingEntry CreateSetting<T>(
        string key,
        MigrationValueKind kind,
        T value) =>
        new()
        {
            Key = key,
            Kind = kind,
            Value = JsonSerializer.SerializeToElement(value, _jsonOptions)
        };

    private static IReadOnlyDictionary<string, object> DecodeSettings(
        IReadOnlyList<MigrationSettingEntry> entries)
    {
        var settings = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ValidateSettingKey(entry.Key);
            object value;
            try
            {
                value = entry.Kind switch
                {
                    MigrationValueKind.String => entry.Value.GetString()
                        ?? throw new InvalidDataException("A migrated string setting is null."),
                    MigrationValueKind.Boolean => entry.Value.GetBoolean(),
                    MigrationValueKind.Byte => entry.Value.GetByte(),
                    MigrationValueKind.SByte => entry.Value.GetSByte(),
                    MigrationValueKind.Int16 => entry.Value.GetInt16(),
                    MigrationValueKind.UInt16 => entry.Value.GetUInt16(),
                    MigrationValueKind.Int32 => entry.Value.GetInt32(),
                    MigrationValueKind.UInt32 => entry.Value.GetUInt32(),
                    MigrationValueKind.Int64 => entry.Value.GetInt64(),
                    MigrationValueKind.UInt64 => entry.Value.GetUInt64(),
                    MigrationValueKind.Single => entry.Value.GetSingle(),
                    MigrationValueKind.Double => entry.Value.GetDouble(),
                    MigrationValueKind.Char => DecodeChar(entry.Value),
                    MigrationValueKind.Guid => entry.Value.GetGuid(),
                    MigrationValueKind.DateTimeOffset => entry.Value.GetDateTimeOffset(),
                    MigrationValueKind.TimeSpan => entry.Value.Deserialize<TimeSpan>(),
                    _ => throw new InvalidDataException("A migrated setting has an unknown value type.")
                };
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            {
                throw new InvalidDataException(
                    $"Migrated setting '{entry.Key}' has an invalid value.",
                    exception);
            }

            if (!settings.TryAdd(entry.Key, value))
            {
                throw new InvalidDataException(
                    $"The identity migration contains duplicate setting '{entry.Key}'.");
            }
        }

        return settings;
    }

    private static char DecodeChar(JsonElement value)
    {
        var text = value.GetString();
        if (text is null || text.Length != 1)
        {
            throw new InvalidDataException("A migrated character setting is invalid.");
        }

        return text[0];
    }

    private static void ValidateSettingKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumSettingKeyLength)
        {
                throw new InvalidDataException("A Package Pilot application-setting key is invalid.");
        }

        if (string.Equals(key, CompletionMarkerSettingKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The identity migration contains a reserved application-setting key.");
        }
    }

    private static string CreateCompletionMarker(MigrationEnvelope envelope)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{ProductMarker}\n{envelope.SchemaVersion}\n{envelope.SourcePackageIdentity}");
        return $"v{envelope.SchemaVersion}:{Convert.ToHexString(SHA256.HashData(input))}";
    }

    private async Task WriteJsonAtomicallyAsync<T>(
        string filePath,
        T value,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = GetTemporaryPath(filePath);
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        value,
                        _jsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > maximumBytes)
                {
                    throw new InvalidDataException(
                        "The Package Pilot identity migration data is too large.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task<bool> RecoverInterruptedExportAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_migrationFilePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(_migrationFilePath)!;
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var candidates = Directory
            .EnumerateFiles(directory, GetTemporarySearchPattern(_migrationFilePath))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = await ReadAndValidateEnvelopeAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(candidate, _migrationFilePath, overwrite: false);
                return true;
            }
            catch (InvalidDataException)
            {
                TryDelete(candidate);
            }
        }

        return false;
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_migrationFilePath)!;
        Directory.CreateDirectory(directory);
        var lockPath = _migrationFilePath + ".lock";
        var started = Stopwatch.GetTimestamp();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < LockTimeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        while (Stopwatch.GetElapsedTime(started) < LockTimeout);

        throw new IOException("The Package Pilot identity migration is already in progress.");
    }

    private void DeleteInterruptedExportFiles()
    {
        var directory = Path.GetDirectoryName(_migrationFilePath)!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     GetTemporarySearchPattern(_migrationFilePath)))
        {
            TryDelete(path);
        }
    }

    private static string GetTemporaryPath(string filePath) =>
        Path.Combine(
            Path.GetDirectoryName(filePath)!,
            $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

    private static string GetTemporarySearchPattern(string filePath) =>
        $".{Path.GetFileName(filePath)}.*.tmp";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A concurrent process still owns the path; the next migration pass retries cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup failure must not hide the migration's primary result.
        }
    }

    private sealed record MigrationEnvelope
    {
        public int SchemaVersion { get; init; }
        public string Product { get; init; } = string.Empty;
        public string SourcePackageIdentity { get; init; } = string.Empty;
        public DateTimeOffset ExportedAtUtc { get; init; }
        public IReadOnlyList<MigrationSettingEntry> Settings { get; init; } = [];
        public JsonElement OperationHistory { get; init; }
    }

    private sealed record MigrationSettingEntry
    {
        public string Key { get; init; } = string.Empty;
        public MigrationValueKind Kind { get; init; }
        public JsonElement Value { get; init; }
    }

    private enum MigrationValueKind
    {
        String,
        Boolean,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Char,
        Guid,
        DateTimeOffset,
        TimeSpan
    }
}
