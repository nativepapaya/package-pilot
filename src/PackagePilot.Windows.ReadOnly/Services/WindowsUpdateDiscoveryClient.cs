using Microsoft.Management.Deployment;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using PackagePilot.Core.Services;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace PackagePilot.Windows.Services;

/// <summary>
/// Performs update discovery directly through the read-only WinGet COM catalog surface.
/// This assembly deliberately has no reference to PackagePilot.Windows or its mutation services.
/// </summary>
public sealed class WindowsUpdateDiscoveryClient : IUpdateDiscoveryClient
{
    private const string ContractName =
        "Microsoft.Management.Deployment.WindowsPackageManagerContract";
    private const uint RequiredContractVersion = 6;

    private readonly object _managerLock = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<PackageSummary>>>? _queryOverride;
    private PackageManager? _manager;

    public WindowsUpdateDiscoveryClient()
    {
    }

    internal WindowsUpdateDiscoveryClient(
        Func<CancellationToken, Task<IReadOnlyList<PackageSummary>>> queryOverride)
    {
        _queryOverride = queryOverride ?? throw new ArgumentNullException(nameof(queryOverride));
    }

    public Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(
        CancellationToken cancellationToken = default) =>
        _queryOverride is null
            ? GetAvailableUpdatesCoreAsync(tryCombinedCatalog: true, cancellationToken)
            : _queryOverride(cancellationToken);

    /// <summary>
    /// Reference implementation retained for the opt-in live equivalence test.
    /// </summary>
    internal Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesPerSourceAsync(
        CancellationToken cancellationToken = default) =>
        GetAvailableUpdatesCoreAsync(tryCombinedCatalog: false, cancellationToken);

    private async Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesCoreAsync(
        bool tryCombinedCatalog,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureSupportedAsync(cancellationToken);

            var manager = GetManager();
            var sources = EnumerateWinRt(manager.GetPackageCatalogs())
                .Select(reference => new RemoteCatalog(reference, GetSourceIdentity(reference)))
                .ToArray();
            if (sources.Length == 0)
            {
                throw CreateFailure(
                    WingetErrorKind.ComFailure,
                    "NoPackageSources",
                    "WinGet did not return any package sources, so update discovery could not be completed.");
            }

            var updates = tryCombinedCatalog
                ? await QueryCombinedThenFallbackAsync(
                    sources,
                    (catalogs, token) => TryFindCombinedUpdatesAsync(manager, catalogs, token),
                    (source, token) => TryFindSourceUpdatesAsync(manager, source, token),
                    cancellationToken)
                : await QueryPerSourceAsync(
                    sources,
                    (source, token) => TryFindSourceUpdatesAsync(manager, source, token),
                    cancellationToken);

            return updates
                .GroupBy(package => package.Key)
                .Select(group => group.First())
                .OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(package => package.SourceName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(package => package.Key.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Key.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WingetException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new WingetException(FromException(exception), exception);
        }
    }

    internal static async Task<IReadOnlyList<TResult>> QueryCombinedThenFallbackAsync<TSource, TResult>(
        IReadOnlyList<TSource> sources,
        Func<IReadOnlyList<TSource>, CancellationToken, Task<IReadOnlyList<TResult>?>> tryCombined,
        Func<TSource, CancellationToken, Task<IReadOnlyList<TResult>?>> trySingle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(tryCombined);
        ArgumentNullException.ThrowIfNull(trySingle);
        cancellationToken.ThrowIfCancellationRequested();

        if (sources.Count > 1)
        {
            var combined = await tryCombined(sources, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (combined is not null)
            {
                return combined;
            }
        }

        return await QueryPerSourceAsync(sources, trySingle, cancellationToken);
    }

    internal static async Task<IReadOnlyList<TResult>> QueryPerSourceAsync<TSource, TResult>(
        IReadOnlyList<TSource> sources,
        Func<TSource, CancellationToken, Task<IReadOnlyList<TResult>?>> trySingle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(trySingle);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<TResult>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceResults = await trySingle(source, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceResults is null)
            {
                throw new InvalidOperationException(
                    "A WinGet package source query did not return a result.");
            }

            results.AddRange(sourceResults);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    internal static bool UpdatesHaveKnownSources(
        IEnumerable<PackageSummary> updates,
        IEnumerable<string> knownSourceIds)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(knownSourceIds);

        var known = knownSourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return updates.All(update =>
            !string.IsNullOrWhiteSpace(update.Key.SourceId)
            && known.Contains(update.Key.SourceId));
    }

    internal static bool HasSingleAttributedSource(
        IEnumerable<(string? Id, string? Name)> versionSources)
    {
        ArgumentNullException.ThrowIfNull(versionSources);

        (string? Id, string? Name)? first = null;
        foreach (var source in versionSources)
        {
            if (string.IsNullOrWhiteSpace(source.Id)
                && string.IsNullOrWhiteSpace(source.Name))
            {
                return false;
            }

            if (first is null)
            {
                first = source;
                continue;
            }

            var sameSource = !string.IsNullOrWhiteSpace(first.Value.Id)
                && !string.IsNullOrWhiteSpace(source.Id)
                    ? string.Equals(first.Value.Id, source.Id, StringComparison.OrdinalIgnoreCase)
                    : SourceAliasesOverlap(
                        first.Value.Id,
                        first.Value.Name,
                        source.Id,
                        source.Name);
            if (!sameSource)
            {
                return false;
            }
        }

        return first is not null;
    }

    internal static bool SourceAliasesOverlap(
        string? firstId,
        string? firstName,
        string? secondId,
        string? secondName)
    {
        var firstAliases = new[] { firstId, firstName }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var secondAliases = new[] { secondId, secondName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return firstAliases.Any(secondAliases.Contains);
    }

    private async Task<IReadOnlyList<PackageSummary>?> TryFindCombinedUpdatesAsync(
        PackageManager manager,
        IReadOnlyList<RemoteCatalog> sources,
        CancellationToken cancellationToken)
    {
        try
        {
            var updates = await FindUpdatesAsync(
                manager,
                sources.Select(source => source.Reference),
                new SourceIdentity(string.Empty, string.Empty),
                cancellationToken,
                sources.Select(source => source.Identity).ToArray());
            if (updates is null)
            {
                return null;
            }

            return UpdatesHaveKnownSources(
                updates,
                sources.Select(source => source.Identity.Id))
                    ? updates
                    : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task<IReadOnlyList<PackageSummary>?> TryFindSourceUpdatesAsync(
        PackageManager manager,
        RemoteCatalog source,
        CancellationToken cancellationToken) =>
        await FindUpdatesAsync(
            manager,
            [source.Reference],
            source.Identity,
            cancellationToken)
        ?? throw new InvalidOperationException(
            $"The WinGet source '{source.Identity.Name}' did not return an update result.");

    private async Task<IReadOnlyList<PackageSummary>?> FindUpdatesAsync(
        PackageManager manager,
        IEnumerable<PackageCatalogReference> remoteReferences,
        SourceIdentity fallbackIdentity,
        CancellationToken cancellationToken,
        IReadOnlyList<SourceIdentity>? combinedSources = null)
    {
        var options = new CreateCompositePackageCatalogOptions
        {
            CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs,
            InstalledScope = PackageInstallScope.Any
        };
        foreach (var remoteReference in remoteReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.Catalogs.Add(remoteReference);
        }

        var reference = manager.CreateCompositePackageCatalog(options);
        var connectResult = await ConnectAsync(reference, cancellationToken);
        if (connectResult.Status != ConnectResultStatus.Ok)
        {
            throw ConnectFailure(connectResult, fallbackIdentity);
        }

        var findResult = await FindAsync(
            connectResult.PackageCatalog,
            new FindPackagesOptions(),
            cancellationToken);
        var updates = new List<PackageSummary>();
        foreach (var match in EnumerateWinRt(findResult.Matches))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!match.CatalogPackage.IsUpdateAvailable)
            {
                continue;
            }

            var summarySource = fallbackIdentity;
            if (combinedSources is not null)
            {
                var versionSources = EnumerateWinRt(match.CatalogPackage.AvailableVersions)
                    .Select(version => TryGetVersionSource(
                        match.CatalogPackage.GetPackageVersionInfo(version)))
                    .Append(TryGetVersionSource(match.CatalogPackage.DefaultInstallVersion))
                    .Select(source => (source?.Id, source?.Name))
                    .ToArray();
                if (!HasSingleAttributedSource(versionSources))
                {
                    return null;
                }

                var actualSource = TryGetVersionSource(
                    match.CatalogPackage.DefaultInstallVersion);
                summarySource = FindConfiguredSource(actualSource, combinedSources)
                    ?? new SourceIdentity(string.Empty, string.Empty);
                if (string.IsNullOrWhiteSpace(summarySource.Id))
                {
                    return null;
                }
            }

            updates.Add(ToSummary(match.CatalogPackage, summarySource));
        }

        return updates;
    }

    private Task EnsureSupportedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var manager = GetManager();
            var catalogs = manager.GetPackageCatalogs();
            var contractVersion = GetHighestContractVersion();
            if (contractVersion == 0 && SupportsContractSix(catalogs))
            {
                contractVersion = RequiredContractVersion;
            }

            // Successful activation still distinguishes an old contract from a
            // missing App Installer installation.
            contractVersion = Math.Max(contractVersion, 1);
            if (contractVersion < RequiredContractVersion)
            {
                throw CreateFailure(
                    WingetErrorKind.ContractTooOld,
                    $"Contract{contractVersion}",
                    "App Installer is too old. Update it to use Package Pilot.");
            }

            return Task.CompletedTask;
        }
        catch (WingetException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new WingetException(FromException(exception), exception);
        }
    }

    private PackageManager GetManager()
    {
        lock (_managerLock)
        {
            return _manager ??= new PackageManager();
        }
    }

    private static uint GetHighestContractVersion()
    {
        for (var version = 29; version >= 1; version--)
        {
            if (ApiInformation.IsApiContractPresent(ContractName, (ushort)version))
            {
                return (uint)version;
            }
        }

        return 0;
    }

    private static bool SupportsContractSix(IReadOnlyList<PackageCatalogReference> catalogs)
    {
        try
        {
            if (catalogs.Count == 0)
            {
                return false;
            }

            _ = catalogs[0].SourceAgreements.Count;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<ConnectResult> ConnectAsync(
        PackageCatalogReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = await Task.Run(reference.ConnectAsync, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await AwaitAsync(operation, cancellationToken);
    }

    private static async Task<FindPackagesResult> FindAsync(
        PackageCatalog catalog,
        FindPackagesOptions options,
        CancellationToken cancellationToken)
    {
        var result = await AwaitAsync(catalog.FindPackagesAsync(options), cancellationToken);
        if (result.Status != FindPackagesResultStatus.Ok)
        {
            throw new WingetException(MapFindError(result));
        }

        return result;
    }

    private static async Task<T> AwaitAsync<T>(
        IAsyncOperation<T> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(operation.Cancel);
        return await operation;
    }

    private static PackageSummary ToSummary(
        CatalogPackage package,
        SourceIdentity fallbackSource)
    {
        var available = package.DefaultInstallVersion;
        var installed = package.InstalledVersion;
        var metadata = TryGetMetadata(available) ?? TryGetMetadata(installed);
        var source = string.IsNullOrWhiteSpace(fallbackSource.Id)
            ? TryGetVersionSource(available) ?? fallbackSource
            : fallbackSource;

        return new PackageSummary
        {
            Key = new PackageKey(package.Id, source.Id),
            Name = FirstNonEmpty(metadata?.PackageName, package.Name, package.Id),
            Publisher = FirstNonEmpty(
                metadata?.Publisher,
                TryGetPublisher(available),
                TryGetPublisher(installed)),
            Description = FirstNonEmpty(metadata?.ShortDescription, metadata?.Description),
            SourceName = source.Name,
            InstalledVersion = NullIfWhiteSpace(installed?.Version),
            AvailableVersion = NullIfWhiteSpace(available?.Version),
            IconUri = SelectIcon(metadata),
            Status = PackageStatus.UpdateAvailable
        };
    }

    private static CatalogPackageMetadata? TryGetMetadata(PackageVersionInfo? version)
    {
        if (version is null)
        {
            return null;
        }

        try
        {
            return version.GetCatalogPackageMetadata();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TryGetPublisher(PackageVersionInfo? version)
    {
        if (version is null)
        {
            return string.Empty;
        }

        try
        {
            return version.Publisher ?? string.Empty;
        }
        catch (Exception)
        {
            try
            {
                return version.GetMetadata(PackageVersionMetadataField.PublisherDisplayName)
                    ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    private static SourceIdentity? TryGetVersionSource(PackageVersionInfo? version)
    {
        if (version is null)
        {
            return null;
        }

        try
        {
            var info = version.PackageCatalog.Info;
            var id = FirstNonEmpty(info.Id, info.Name);
            return string.IsNullOrWhiteSpace(id)
                ? null
                : new SourceIdentity(id, FirstNonEmpty(info.Name, info.Id));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Uri? SelectIcon(CatalogPackageMetadata? metadata)
    {
        if (metadata?.Icons is null)
        {
            return null;
        }

        var icon = EnumerateWinRt(metadata.Icons)
            .OrderByDescending(item => item.Theme == IconTheme.Default)
            .ThenByDescending(item => item.Resolution == IconResolution.Square64)
            .FirstOrDefault();
        return Uri.TryCreate(icon?.Url, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static SourceIdentity? FindConfiguredSource(
        SourceIdentity? actualSource,
        IReadOnlyList<SourceIdentity> configuredSources)
    {
        if (actualSource is null)
        {
            return null;
        }

        var idMatches = configuredSources
            .Where(source => !string.IsNullOrWhiteSpace(actualSource.Id)
                && (string.Equals(source.Id, actualSource.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(source.Name, actualSource.Id, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        if (idMatches.Length == 1)
        {
            return idMatches[0];
        }

        var nameMatches = configuredSources
            .Where(source => !string.IsNullOrWhiteSpace(actualSource.Name)
                && (string.Equals(source.Id, actualSource.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(source.Name, actualSource.Name, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        return nameMatches.Length == 1 ? nameMatches[0] : null;
    }

    private static SourceIdentity GetSourceIdentity(
        PackageCatalogReference reference,
        string fallbackName = "Package source",
        string fallbackId = "unknown")
    {
        try
        {
            var info = reference.Info;
            return new SourceIdentity(
                FirstNonEmpty(info.Id, info.Name, fallbackId),
                FirstNonEmpty(info.Name, info.Id, fallbackName));
        }
        catch (Exception)
        {
            return new SourceIdentity(fallbackId, fallbackName);
        }
    }

    private static WingetException ConnectFailure(
        ConnectResult result,
        SourceIdentity source)
    {
        if (result.Status == ConnectResultStatus.SourceAgreementsNotAccepted)
        {
            return CreateFailure(
                WingetErrorKind.AgreementRequired,
                result.Status.ToString(),
                $"The {source.Name} source requires agreement acceptance.");
        }

        var hresult = TryGetHResult(() => result.ExtendedErrorCode);
        return CreateFailure(
            result.Status == ConnectResultStatus.CatalogError
                ? WingetErrorKind.Network
                : WingetErrorKind.ComFailure,
            result.Status.ToString(),
            result.Status == ConnectResultStatus.CatalogError
                ? $"Package Pilot could not connect to {source.Name}. Check the network and source configuration."
                : $"Package Pilot could not open the {source.Name} source.",
            hresult);
    }

    private static WingetError MapFindError(FindPackagesResult result)
    {
        var hresult = TryGetHResult(() => result.ExtendedErrorCode);
        var (kind, message) = result.Status switch
        {
            FindPackagesResultStatus.BlockedByPolicy => (WingetErrorKind.PolicyBlocked,
                "Package search is blocked by your organization's policy."),
            FindPackagesResultStatus.AuthenticationError => (WingetErrorKind.Authentication,
                "The package source requires authentication."),
            FindPackagesResultStatus.AccessDenied => (WingetErrorKind.PolicyBlocked,
                "Access to the package source was denied."),
            FindPackagesResultStatus.CatalogError => (WingetErrorKind.Network,
                "The package source could not be reached."),
            FindPackagesResultStatus.InvalidOptions => (WingetErrorKind.ComFailure,
                "The package search options were not valid."),
            _ => (ClassifyHResult(hresult), "WinGet could not search the package catalog.")
        };

        return new WingetError
        {
            Kind = kind,
            Code = result.Status.ToString(),
            Message = message,
            HResult = hresult
        };
    }

    private static WingetError FromException(Exception exception)
    {
        var hresult = exception.HResult;
        var kind = ClassifyHResult(hresult);
        var message = kind switch
        {
            WingetErrorKind.AppInstallerMissing =>
                "Windows Package Manager is unavailable. Install or update App Installer from the Microsoft Store.",
            WingetErrorKind.PolicyBlocked =>
                "Windows Package Manager is blocked by your organization's policy.",
            WingetErrorKind.Authentication =>
                "The package source requires authentication.",
            WingetErrorKind.Network =>
                "The package source could not be reached. Check your connection and try again.",
            WingetErrorKind.Cancelled =>
                "The operation was cancelled.",
            _ => "Windows Package Manager encountered an unexpected error."
        };

        return new WingetError
        {
            Kind = kind,
            Code = $"0x{unchecked((uint)hresult):X8}",
            Message = message,
            HResult = hresult
        };
    }

    private static WingetErrorKind ClassifyHResult(int? hresult)
    {
        if (hresult is null)
        {
            return WingetErrorKind.Unknown;
        }

        return unchecked((uint)hresult.Value) switch
        {
            0x80040154 => WingetErrorKind.AppInstallerMissing,
            0x80070005 => WingetErrorKind.PolicyBlocked,
            0x800704C7 => WingetErrorKind.Cancelled,
            0x80072EE2 or 0x80072EE7 or 0x80072EFD or 0x80072EFE => WingetErrorKind.Network,
            0x800704C6 => WingetErrorKind.Authentication,
            0x800704C8 => WingetErrorKind.Cancelled,
            _ => WingetErrorKind.ComFailure
        };
    }

    private static WingetException CreateFailure(
        WingetErrorKind kind,
        string code,
        string message,
        int? hresult = null) =>
        new(new WingetError
        {
            Kind = kind,
            Code = code,
            Message = message,
            HResult = hresult
        });

    private static int? TryGetHResult(Func<Exception?> getValue)
    {
        try
        {
            var value = getValue()?.HResult;
            return value is null or 0 ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<T> EnumerateWinRt<T>(IReadOnlyList<T> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            yield return values[index];
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? string.Empty;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record SourceIdentity(string Id, string Name);

    private sealed record RemoteCatalog(PackageCatalogReference Reference, SourceIdentity Identity);
}
