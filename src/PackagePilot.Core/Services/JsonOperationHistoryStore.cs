using System.Text.Json;
using System.Text.Json.Serialization;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>Persists operation history as an atomically replaced, human-readable JSON file.</summary>
public sealed class JsonOperationHistoryStore : IOperationHistoryStore
{
    private readonly string _filePath;
    private readonly int _historyLimit;
    private readonly JsonSerializerOptions _options;

    public JsonOperationHistoryStore(string filePath, int historyLimit = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(historyLimit, 1);

        _filePath = Path.GetFullPath(filePath);
        _historyLimit = Math.Min(historyLimit, 100);
        _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<IReadOnlyList<OperationResult>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<OperationResult>();
        }

        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var results = await JsonSerializer.DeserializeAsync<List<OperationResult>>(
                stream,
                _options,
                cancellationToken).ConfigureAwait(false);

            return (results ?? [])
                .Take(_historyLimit)
                .Select(MigrateLegacyResult)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Package Pilot operation history file is invalid.", exception);
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<OperationResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);

        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

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
                    results.Take(_historyLimit).Select(MigrateLegacyResult).ToArray(),
                    _options,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    private static OperationResult MigrateLegacyResult(OperationResult result)
    {
        var migrated = result.Target is null && !result.Package.IsEmpty
            ? result with { Target = new WingetTarget { Package = result.Package } }
            : result;

        var diagnostic = migrated.Diagnostic;
        if (diagnostic is not null
            && (diagnostic.ReferenceId == Guid.Empty
                || !Enum.IsDefined(diagnostic.Provider)
                || (diagnostic.Provider == OperationDiagnosticProvider.Winget
                    && (diagnostic.ReferenceId != migrated.OperationId
                        || migrated.EffectiveTarget is not WingetTarget))
                || (diagnostic.Provider == OperationDiagnosticProvider.WindowsDeployment
                    && migrated.EffectiveTarget is not MsixTarget)))
        {
            diagnostic = null;
        }

        if (diagnostic is null
            && migrated.EffectiveTarget is WingetTarget
            && migrated.OperationId != Guid.Empty)
        {
            diagnostic = new OperationDiagnosticReference
            {
                Provider = OperationDiagnosticProvider.Winget,
                ReferenceId = migrated.OperationId
            };
        }

        return migrated with { Diagnostic = diagnostic };
    }
}
