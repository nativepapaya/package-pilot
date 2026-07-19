using System.Text.Json;
using System.Text.Json.Serialization;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;

namespace PackagePilot.Core.Services;

/// <summary>
/// Persists display-only installed inventory using atomic same-directory replacement. Mutation
/// descriptors and direct MSIX removal references are stripped both when saving and loading.
/// </summary>
public sealed class JsonInstalledAppSnapshotStore : IInstalledAppSnapshotStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _options;

    public JsonInstalledAppSnapshotStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<InstalledAppSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var snapshot = await JsonSerializer.DeserializeAsync<InstalledAppSnapshot>(
                stream,
                _options,
                cancellationToken).ConfigureAwait(false);
            return snapshot?.SchemaVersion == InstalledAppSnapshot.CurrentSchemaVersion
                ? ToDisplayOnly(snapshot)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        InstalledAppSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var displaySnapshot = ToDisplayOnly(snapshot);
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
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    displaySnapshot,
                    _options,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
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

    internal static InstalledAppSnapshot ToDisplayOnly(InstalledAppSnapshot snapshot) =>
        snapshot with
        {
            Apps = snapshot.Apps.Select(app => app with
            {
                Actions = Array.Empty<InstalledAppActionDescriptor>(),
                Installations = app.Installations.Select(installation => installation with
                {
                    PackageFullName = null,
                    SupportsDirectRemoval = false
                }).ToArray()
            }).ToArray()
        };
}
