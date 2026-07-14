using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;
using PackagePilot.Core.Abstractions;
using PackagePilot.Core.Models;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.System;
using CoreElevationRequirement = PackagePilot.Core.Models.ElevationRequirement;
using CorePackageAgreement = PackagePilot.Core.Models.PackageAgreement;
using CorePackageMatchField = PackagePilot.Core.Models.PackageMatchField;
using DeploymentElevationRequirement = Microsoft.Management.Deployment.ElevationRequirement;

namespace PackagePilot.App.Services;

/// <summary>
/// Out-of-process adapter for the Windows Package Manager deployment API.
/// </summary>
public sealed class WingetClient : IWingetClient
{
    private const string ContractName =
        "Microsoft.Management.Deployment.WindowsPackageManagerContract";

    private readonly object _managerLock = new();
    private PackageManager? _manager;

    public Task<WingetCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var contractVersion = GetHighestContractVersion();
        try
        {
            var manager = GetManager();
            var catalogs = manager.GetPackageCatalogs();

            string? version = null;
            if (contractVersion >= 13 || contractVersion == 0)
            {
                try
                {
                    version = NullIfWhiteSpace(manager.Version);
                    contractVersion = Math.Max(
                        contractVersion,
                        InferContractVersion(version, minimum: 13));
                }
                catch (Exception)
                {
                    // Version was added in contract 13. A servicing mismatch must not hide
                    // otherwise usable V1 capability information.
                }
            }

            // ApiInformation can return false for third-party WinRT contracts in an
            // unpackaged host even though the registered out-of-process server is
            // available. Probe the first contract-6 member read-only before treating
            // that as an old App Installer installation.
            if (contractVersion == 0 && SupportsContractSix(catalogs))
            {
                contractVersion = WingetCapabilities.RequiredContractVersion;
            }

            // Successful activation still distinguishes a genuinely old contract
            // from a missing App Installer installation.
            contractVersion = Math.Max(contractVersion, 1);

            return Task.FromResult(new WingetCapabilities
            {
                IsAvailable = true,
                ContractVersion = contractVersion,
                Version = version,
                UnavailableReason = contractVersion < WingetCapabilities.RequiredContractVersion
                    ? "App Installer is too old. Package Pilot requires WinGet API contract 6 or newer."
                    : null
            });
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var error = FromException(exception);
            return Task.FromResult(new WingetCapabilities
            {
                IsAvailable = false,
                ContractVersion = contractVersion,
                UnavailableReason = error.Message
            });
        }
    }

    public async Task<IReadOnlyList<PackageSourceStatus>> GetSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSupportedAsync(cancellationToken);

        var statuses = new List<PackageSourceStatus>();
        foreach (var reference in EnumerateWinRt(GetManager().GetPackageCatalogs()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = GetSourceIdentity(reference);

            try
            {
                var result = await AwaitAsync(reference.ConnectAsync(), cancellationToken);
                statuses.Add(ToSourceStatus(
                    identity,
                    result,
                    result.Status == ConnectResultStatus.SourceAgreementsNotAccepted
                        ? GetSourceAgreements(reference, identity)
                        : Array.Empty<CorePackageAgreement>()));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                var error = FromException(exception);
                statuses.Add(new PackageSourceStatus
                {
                    Id = identity.Id,
                    Name = identity.Name,
                    Health = ToSourceHealth(error.Kind),
                    Message = error.Message
                });
            }
        }

        return statuses;
    }

    public async Task<PackageSearchResult> SearchAsync(
        PackageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureSupportedAsync(cancellationToken);

        var packages = new List<PackageSummary>();
        var sources = new List<PackageSourceStatus>();
        var truncated = false;

        foreach (var reference in SelectRemoteCatalogs(query.SourceId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = GetSourceIdentity(reference);

            try
            {
                // This flag is only true after the UI has displayed the source terms
                // and captured explicit user consent.
                if (query.AcceptSourceAgreements)
                {
                    reference.AcceptSourceAgreements = true;
                }
                var connectResult = await AwaitAsync(reference.ConnectAsync(), cancellationToken);
                var sourceStatus = ToSourceStatus(
                    identity,
                    connectResult,
                    connectResult.Status == ConnectResultStatus.SourceAgreementsNotAccepted
                        ? GetSourceAgreements(reference, identity)
                        : Array.Empty<CorePackageAgreement>());
                sources.Add(sourceStatus);

                if (connectResult.Status != ConnectResultStatus.Ok)
                {
                    continue;
                }

                var options = CreateFindOptions(query);
                var result = await AwaitAsync(
                    connectResult.PackageCatalog.FindPackagesAsync(options),
                    cancellationToken);

                if (result.Status != FindPackagesResultStatus.Ok)
                {
                    sources[^1] = new PackageSourceStatus
                    {
                        Id = identity.Id,
                        Name = identity.Name,
                        Health = MapFindHealth(result.Status),
                        Message = DescribeFindFailure(result.Status, TryGetFindHResult(result))
                    };
                    continue;
                }

                truncated |= result.WasLimitExceeded;
                foreach (var match in EnumerateWinRt(result.Matches))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    packages.Add(ToSummary(match.CatalogPackage, identity));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                var error = FromException(exception);
                sources.Add(new PackageSourceStatus
                {
                    Id = identity.Id,
                    Name = identity.Name,
                    Health = ToSourceHealth(error.Kind),
                    Message = error.Message
                });
            }
        }

        return new PackageSearchResult
        {
            Packages = packages
                .GroupBy(package => package.Key)
                .Select(group => group.First())
                .Take(query.Limit)
                .ToArray(),
            Sources = sources,
            IsTruncated = truncated || packages.Count > query.Limit
        };
    }

    public async Task<IReadOnlyList<PackageSummary>> GetInstalledPackagesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSupportedAsync(cancellationToken);

        var reference = GetManager().GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);
        var identity = GetSourceIdentity(reference, "Installed", "installed");
        var catalog = await ConnectOrThrowAsync(reference, cancellationToken);
        var result = await FindAsync(catalog, CreateFindAllOptions(), cancellationToken);

        return EnumerateWinRt(result.Matches)
            .Select(match => ToSummary(match.CatalogPackage, identity, PackageStatus.Installed))
            .OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSupportedAsync(cancellationToken);

        var manager = GetManager();
        var updates = new List<PackageSummary>();

        foreach (var remoteReference in EnumerateWinRt(manager.GetPackageCatalogs()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = GetSourceIdentity(remoteReference);

            try
            {
                var options = new CreateCompositePackageCatalogOptions
                {
                    // Updates are an installed-inventory query enriched by the remote
                    // source. Searching all remote packages with an empty selector can
                    // enumerate the entire catalog before correlation and may never
                    // return promptly on large sources.
                    CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs,
                    InstalledScope = PackageInstallScope.Any
                };
                options.Catalogs.Add(remoteReference);

                var reference = manager.CreateCompositePackageCatalog(options);
                var connectResult = await AwaitAsync(reference.ConnectAsync(), cancellationToken);
                if (connectResult.Status != ConnectResultStatus.Ok)
                {
                    continue;
                }

                var findResult = await FindAsync(
                    connectResult.PackageCatalog,
                    CreateFindAllOptions(),
                    cancellationToken);

                foreach (var match in EnumerateWinRt(findResult.Matches))
                {
                    if (match.CatalogPackage.IsUpdateAvailable)
                    {
                        updates.Add(ToSummary(
                            match.CatalogPackage,
                            identity,
                            PackageStatus.UpdateAvailable));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // One unavailable source must not hide updates from healthy sources.
            }
        }

        return updates
            .GroupBy(package => package.Key)
            .Select(group => group.First())
            .OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<PackageDetails?> GetPackageDetailsAsync(
        PackageKey package,
        InstallPreferences? preferences = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.IsEmpty)
        {
            return null;
        }

        await EnsureSupportedAsync(cancellationToken);

        try
        {
            var resolved = await ResolvePackageAsync(
                package,
                PackageOperationKind.Install,
                preferences ?? new InstallPreferences(),
                cancellationToken);

            return ToDetails(resolved.Package, resolved.Source, Array.Empty<CorePackageAgreement>());
        }
        catch (WingetClientException exception)
            when (exception.Error.Kind == WingetErrorKind.AgreementRequired)
        {
            return new PackageDetails
            {
                Summary = new PackageSummary
                {
                    Key = package,
                    Name = package.Id,
                    SourceName = package.SourceId,
                    Status = PackageStatus.Unavailable
                },
                Agreements = exception.Agreements
            };
        }
        catch (WingetClientException exception)
            when (exception.Error.Kind == WingetErrorKind.PackageNotFound)
        {
            return null;
        }
    }

    public Task<OperationResult> InstallAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunInstallOrUpgradeAsync(operation, isUpgrade: false, progress, cancellationToken);

    public Task<OperationResult> UpgradeAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunInstallOrUpgradeAsync(operation, isUpgrade: true, progress, cancellationToken);

    public async Task<OperationResult> UninstallAsync(
        PackageOperation operation,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var startedAt = DateTimeOffset.UtcNow;
        Report(progress, operation.Id, PackageOperationState.Resolving, null,
            "Finding the installed package…");

        try
        {
            await EnsureSupportedAsync(cancellationToken);
            var resolved = await ResolvePackageAsync(
                operation.Package,
                PackageOperationKind.Uninstall,
                operation.Preferences,
                cancellationToken);

            var options = new UninstallOptions
            {
                CorrelationData = CreateCorrelationData(operation.Id),
                PackageUninstallMode = PackageUninstallMode.Default,
                PackageUninstallScope = ToUninstallScope(operation.Preferences.Scope)
            };

            var asyncOperation = GetManager().UninstallPackageAsync(resolved.Package, options);
            var canCancel = 1;
            asyncOperation.Progress = (_, value) =>
            {
                var state = ToUninstallState(value.State);
                if (state == PackageOperationState.Uninstalling)
                {
                    Interlocked.Exchange(ref canCancel, 0);
                }

                Report(progress, operation.Id, state,
                    NormalizePercent(value.UninstallationProgress),
                    DescribeState(state));
            };

            using var registration = RegisterCancelable(
                asyncOperation,
                cancellationToken,
                () => Volatile.Read(ref canCancel) == 1);
            var result = await asyncOperation;
            return ToUninstallResult(operation, startedAt, result);
        }
        catch (OperationCanceledException exception)
        {
            return CancelledResult(operation, startedAt, exception);
        }
        catch (WingetClientException exception)
        {
            return FailedResult(operation, startedAt, exception.Error);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return FailedResult(operation, startedAt, FromException(exception));
        }
    }

    private async Task<OperationResult> RunInstallOrUpgradeAsync(
        PackageOperation operation,
        bool isUpgrade,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var startedAt = DateTimeOffset.UtcNow;
        Report(progress, operation.Id, PackageOperationState.Resolving, null,
            isUpgrade ? "Finding the update…" : "Finding the package…");

        try
        {
            await EnsureSupportedAsync(cancellationToken);
            var resolved = await ResolvePackageAsync(
                operation.Package,
                isUpgrade ? PackageOperationKind.Upgrade : PackageOperationKind.Install,
                operation.Preferences,
                cancellationToken);

            var installOptions = CreateInstallOptions(operation.Preferences, operation.Id);
            var packageAgreements = GetPackageAgreements(resolved.Package);
            if (packageAgreements.Count > 0 && !operation.Preferences.AcceptPackageAgreements)
            {
                throw AgreementRequired(
                    "This package requires agreement acceptance before it can be installed.",
                    packageAgreements);
            }

            if (!operation.Preferences.AllowElevation && RequiresElevation(resolved.Package, installOptions))
            {
                throw new WingetClientException(new WingetError
                {
                    Kind = WingetErrorKind.ElevationDenied,
                    Code = "ElevationDisabled",
                    Message = "This installer requires elevation, but elevation is disabled for this operation."
                });
            }

            var manager = GetManager();
            var asyncOperation = isUpgrade
                ? manager.UpgradePackageAsync(resolved.Package, installOptions)
                : manager.InstallPackageAsync(resolved.Package, installOptions);

            var canCancel = 1;
            asyncOperation.Progress = (_, value) =>
            {
                var state = ToInstallState(value.State, isUpgrade);
                if (state is PackageOperationState.Installing or PackageOperationState.Upgrading)
                {
                    Interlocked.Exchange(ref canCancel, 0);
                }

                var percent = value.State == PackageInstallProgressState.Downloading
                    ? NormalizePercent(value.DownloadProgress)
                    : NormalizePercent(value.InstallationProgress);
                Report(
                    progress,
                    operation.Id,
                    state,
                    percent,
                    DescribeState(state),
                    ToNullableLong(value.BytesDownloaded),
                    ToNullableLong(value.BytesRequired));
            };

            using var registration = RegisterCancelable(
                asyncOperation,
                cancellationToken,
                () => Volatile.Read(ref canCancel) == 1);
            var result = await asyncOperation;
            return ToInstallResult(operation, startedAt, result);
        }
        catch (OperationCanceledException exception)
        {
            return CancelledResult(operation, startedAt, exception);
        }
        catch (WingetClientException exception)
        {
            return FailedResult(operation, startedAt, exception.Error);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return FailedResult(operation, startedAt, FromException(exception));
        }
    }

    private async Task<ResolvedPackage> ResolvePackageAsync(
        PackageKey key,
        PackageOperationKind kind,
        InstallPreferences preferences,
        CancellationToken cancellationToken)
    {
        if (key.IsEmpty)
        {
            throw PackageNotFound(key);
        }

        var manager = GetManager();
        if (kind == PackageOperationKind.Uninstall)
        {
            var localReference = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);
            var localSource = GetSourceIdentity(localReference, "Installed", "installed");
            var localCatalog = await ConnectOrThrowAsync(localReference, cancellationToken);
            var package = await FindExactAsync(localCatalog, key.Id, cancellationToken);
            return package is null
                ? throw PackageNotFound(key)
                : new ResolvedPackage(package, localSource);
        }

        var candidates = SelectRemoteCatalogs(key.SourceId).ToArray();
        foreach (var sourceReference in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = GetSourceIdentity(sourceReference);

            PackageCatalogReference reference = sourceReference;
            if (kind == PackageOperationKind.Upgrade)
            {
                var compositeOptions = new CreateCompositePackageCatalogOptions
                {
                    CompositeSearchBehavior = CompositeSearchBehavior.RemotePackagesFromAllCatalogs,
                    InstalledScope = PackageInstallScope.Any
                };
                compositeOptions.Catalogs.Add(sourceReference);
                reference = manager.CreateCompositePackageCatalog(compositeOptions);
            }

            if (preferences.AcceptSourceAgreements)
            {
                reference.AcceptSourceAgreements = true;
            }
            var connectResult = await AwaitAsync(reference.ConnectAsync(), cancellationToken);
            if (connectResult.Status == ConnectResultStatus.SourceAgreementsNotAccepted)
            {
                throw AgreementRequired(
                    $"The {source.Name} source requires agreement acceptance before it can be used.",
                    GetSourceAgreements(reference, source));
            }

            if (connectResult.Status != ConnectResultStatus.Ok)
            {
                if (candidates.Length == 1)
                {
                    throw ConnectFailure(connectResult, source);
                }

                continue;
            }

            var package = await FindExactAsync(
                connectResult.PackageCatalog,
                key.Id,
                cancellationToken);
            if (package is not null)
            {
                return new ResolvedPackage(package, source);
            }
        }

        throw PackageNotFound(key);
    }

    private IEnumerable<PackageCatalogReference> SelectRemoteCatalogs(string? sourceId)
    {
        var references = new List<PackageCatalogReference>();
        foreach (var reference in EnumerateWinRt(GetManager().GetPackageCatalogs()))
        {
            var identity = GetSourceIdentity(reference);
            if (string.IsNullOrWhiteSpace(sourceId)
                || string.Equals(identity.Id, sourceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(identity.Name, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                references.Add(reference);
            }
        }

        return references;
    }

    private static FindPackagesOptions CreateFindOptions(PackageQuery query)
    {
        var options = new FindPackagesOptions { ResultLimit = (uint)query.Limit };
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            options.Selectors.Add(new PackageMatchFilter
            {
                Field = ToDeploymentMatchField(query.MatchField),
                Option = PackageFieldMatchOption.ContainsCaseInsensitive,
                Value = query.SearchText.Trim()
            });
        }

        return options;
    }

    private static FindPackagesOptions CreateFindAllOptions() => new();

    private static async Task<CatalogPackage?> FindExactAsync(
        PackageCatalog catalog,
        string packageId,
        CancellationToken cancellationToken)
    {
        var options = new FindPackagesOptions { ResultLimit = 10 };
        options.Selectors.Add(new PackageMatchFilter
        {
            Field = Microsoft.Management.Deployment.PackageMatchField.Id,
            Option = PackageFieldMatchOption.EqualsCaseInsensitive,
            Value = packageId
        });

        var result = await FindAsync(catalog, options, cancellationToken);
        return EnumerateWinRt(result.Matches)
            .Select(match => match.CatalogPackage)
            .FirstOrDefault(package =>
                string.Equals(package.Id, packageId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<FindPackagesResult> FindAsync(
        PackageCatalog catalog,
        FindPackagesOptions options,
        CancellationToken cancellationToken)
    {
        var result = await AwaitAsync(catalog.FindPackagesAsync(options), cancellationToken);
        if (result.Status != FindPackagesResultStatus.Ok)
        {
            throw new WingetClientException(MapFindError(result));
        }

        return result;
    }

    private static PackageSummary ToSummary(
        CatalogPackage package,
        SourceIdentity fallbackSource,
        PackageStatus? forcedStatus = null)
    {
        var available = package.DefaultInstallVersion;
        var installed = package.InstalledVersion;
        var metadata = TryGetMetadata(available) ?? TryGetMetadata(installed);
        var source = TryGetVersionSource(available) ?? fallbackSource;
        var installer = TryGetInstaller(available, new InstallOptions());

        var status = forcedStatus ?? (package.IsUpdateAvailable
            ? PackageStatus.UpdateAvailable
            : installed is not null
                ? available is null ? PackageStatus.Unmatched : PackageStatus.Installed
                : available is null ? PackageStatus.Unavailable : PackageStatus.Available);

        return new PackageSummary
        {
            Key = new PackageKey(package.Id, source.Id),
            Name = FirstNonEmpty(metadata?.PackageName, package.Name, package.Id),
            Publisher = FirstNonEmpty(metadata?.Publisher, TryGetPublisher(available),
                TryGetPublisher(installed)),
            Description = FirstNonEmpty(metadata?.ShortDescription, metadata?.Description),
            SourceName = source.Name,
            InstalledVersion = NullIfWhiteSpace(installed?.Version),
            AvailableVersion = NullIfWhiteSpace(available?.Version),
            IconUri = SelectIcon(metadata),
            Status = status,
            ElevationRequirement = ToCoreElevation(installer?.ElevationRequirement)
        };
    }

    private static PackageDetails ToDetails(
        CatalogPackage package,
        SourceIdentity source,
        IReadOnlyList<CorePackageAgreement> sourceAgreements)
    {
        var version = package.DefaultInstallVersion ?? package.InstalledVersion;
        var metadata = TryGetMetadata(version);
        var installer = TryGetInstaller(version, new InstallOptions());
        var agreements = sourceAgreements.Concat(GetPackageAgreements(package)).ToArray();

        return new PackageDetails
        {
            Summary = ToSummary(package, source),
            Description = FirstNonEmpty(metadata?.Description, metadata?.ShortDescription),
            Publisher = FirstNonEmpty(metadata?.Publisher, TryGetPublisher(version)),
            License = metadata?.License ?? string.Empty,
            LicenseUri = ToUri(metadata?.LicenseUrl),
            PublisherUri = ToUri(metadata?.PublisherUrl),
            HomepageUri = ToUri(metadata?.PackageUrl),
            SupportUri = ToUri(metadata?.PublisherSupportUrl),
            ReleaseNotes = metadata?.ReleaseNotes ?? string.Empty,
            ReleaseNotesUri = ToUri(metadata?.ReleaseNotesUrl),
            Tags = metadata is null
                ? Array.Empty<string>()
                : EnumerateWinRt(metadata.Tags)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray(),
            Versions = EnumerateWinRt(package.AvailableVersions)
                .Select(item => item.Version)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            InstallerScope = ToCoreScope(installer?.Scope),
            Architecture = ToCoreArchitecture(installer?.Architecture),
            ElevationRequirement = ToCoreElevation(installer?.ElevationRequirement),
            Agreements = agreements
        };
    }

    private static IReadOnlyList<CorePackageAgreement> GetPackageAgreements(CatalogPackage package)
    {
        var metadata = TryGetMetadata(package.DefaultInstallVersion);
        if (metadata?.Agreements is null)
        {
            return Array.Empty<CorePackageAgreement>();
        }

        return EnumerateWinRt(metadata.Agreements).Select((agreement, index) => new CorePackageAgreement
        {
            Id = $"package:{package.Id}:{index}",
            Kind = AgreementKind.Package,
            Label = agreement.Label ?? string.Empty,
            Text = agreement.Text ?? string.Empty,
            AgreementUri = ToUri(agreement.Url),
            RequiresExplicitAcceptance = true
        }).ToArray();
    }

    private static IReadOnlyList<CorePackageAgreement> GetSourceAgreements(
        PackageCatalogReference reference,
        SourceIdentity source)
    {
        try
        {
            return EnumerateWinRt(reference.SourceAgreements).Select((agreement, index) => new CorePackageAgreement
            {
                Id = $"source:{source.Id}:{index}",
                Kind = AgreementKind.Source,
                Label = agreement.Label ?? string.Empty,
                Text = agreement.Text ?? string.Empty,
                AgreementUri = ToUri(agreement.Url),
                RequiresExplicitAcceptance = true
            }).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<CorePackageAgreement>();
        }
    }

    private static InstallOptions CreateInstallOptions(
        InstallPreferences preferences,
        Guid? operationId = null)
    {
        var options = new InstallOptions
        {
            PackageInstallMode = PackageInstallMode.Default,
            PackageInstallScope = ToInstallScope(preferences.Scope),
            AcceptPackageAgreements = preferences.AcceptPackageAgreements,
            CorrelationData = operationId is null ? string.Empty : CreateCorrelationData(operationId.Value)
        };

        var architecture = ToProcessorArchitecture(preferences.Architecture);
        if (architecture is not null)
        {
            options.AllowedArchitectures.Clear();
            options.AllowedArchitectures.Add(architecture.Value);
        }

        return options;
    }

    private static bool RequiresElevation(CatalogPackage package, InstallOptions options)
    {
        var installer = TryGetInstaller(package.DefaultInstallVersion, options);
        return installer?.ElevationRequirement is DeploymentElevationRequirement.ElevationRequired
            or DeploymentElevationRequirement.ElevatesSelf;
    }

    private static PackageInstallerInfo? TryGetInstaller(
        PackageVersionInfo? version,
        InstallOptions options)
    {
        if (version is null)
        {
            return null;
        }

        try
        {
            return version.GetApplicableInstaller(options);
        }
        catch (Exception)
        {
            return null;
        }
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
            return new SourceIdentity(info.Id ?? string.Empty, info.Name ?? string.Empty);
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
        return ToUri(icon?.Url);
    }

    private async Task<PackageCatalog> ConnectOrThrowAsync(
        PackageCatalogReference reference,
        CancellationToken cancellationToken)
    {
        var source = GetSourceIdentity(reference);
        var result = await AwaitAsync(reference.ConnectAsync(), cancellationToken);
        if (result.Status != ConnectResultStatus.Ok)
        {
            throw ConnectFailure(result, source);
        }

        return result.PackageCatalog;
    }

    private static WingetClientException ConnectFailure(
        ConnectResult result,
        SourceIdentity source)
    {
        if (result.Status == ConnectResultStatus.SourceAgreementsNotAccepted)
        {
            return new WingetClientException(new WingetError
            {
                Kind = WingetErrorKind.AgreementRequired,
                Code = result.Status.ToString(),
                Message = $"The {source.Name} source requires agreement acceptance."
            });
        }

        var hresult = TryGetConnectHResult(result);
        return new WingetClientException(new WingetError
        {
            Kind = result.Status == ConnectResultStatus.CatalogError
                ? WingetErrorKind.Network
                : WingetErrorKind.ComFailure,
            Code = result.Status.ToString(),
            Message = result.Status == ConnectResultStatus.CatalogError
                ? $"Package Pilot could not connect to {source.Name}. Check the network and source configuration."
                : $"Package Pilot could not open the {source.Name} source.",
            HResult = hresult
        });
    }

    private static PackageSourceStatus ToSourceStatus(
        SourceIdentity identity,
        ConnectResult result,
        IReadOnlyList<CorePackageAgreement> agreements)
    {
        return new PackageSourceStatus
        {
            Id = identity.Id,
            Name = identity.Name,
            Health = result.Status switch
            {
                ConnectResultStatus.Ok => SourceHealth.Healthy,
                ConnectResultStatus.SourceAgreementsNotAccepted => SourceHealth.Degraded,
                ConnectResultStatus.CatalogError => SourceHealth.Unavailable,
                _ => SourceHealth.Unknown
            },
            Message = result.Status switch
            {
                ConnectResultStatus.Ok => null,
                ConnectResultStatus.SourceAgreementsNotAccepted =>
                    "This source requires agreement acceptance before it can be used.",
                ConnectResultStatus.CatalogError =>
                    "The source could not be reached. Check the network and source configuration.",
                _ => $"The source returned {result.Status}."
            },
            Agreements = agreements
        };
    }

    private static OperationResult ToInstallResult(
        PackageOperation operation,
        DateTimeOffset startedAt,
        InstallResult result)
    {
        if (result.Status == InstallResultStatus.Ok)
        {
            return SuccessfulResult(operation, startedAt, result.RebootRequired);
        }

        return FailedResult(operation, startedAt, MapInstallError(result));
    }

    private static OperationResult ToUninstallResult(
        PackageOperation operation,
        DateTimeOffset startedAt,
        UninstallResult result)
    {
        if (result.Status == UninstallResultStatus.Ok)
        {
            return SuccessfulResult(operation, startedAt, result.RebootRequired);
        }

        return FailedResult(operation, startedAt, MapUninstallError(result));
    }

    private static OperationResult SuccessfulResult(
        PackageOperation operation,
        DateTimeOffset startedAt,
        bool rebootRequired) => new()
    {
        OperationId = operation.Id,
        Package = operation.Package,
        Kind = operation.Kind,
        State = rebootRequired
            ? PackageOperationState.RebootRequired
            : PackageOperationState.Completed,
        StartedAt = startedAt,
        CompletedAt = DateTimeOffset.UtcNow,
        RebootRequired = rebootRequired
    };

    private static OperationResult FailedResult(
        PackageOperation operation,
        DateTimeOffset startedAt,
        WingetError error) => new()
    {
        OperationId = operation.Id,
        Package = operation.Package,
        Kind = operation.Kind,
        State = error.Kind == WingetErrorKind.Cancelled
            ? PackageOperationState.Cancelled
            : PackageOperationState.Failed,
        StartedAt = startedAt,
        CompletedAt = DateTimeOffset.UtcNow,
        Error = error
    };

    private static OperationResult CancelledResult(
        PackageOperation operation,
        DateTimeOffset startedAt,
        Exception exception) => FailedResult(operation, startedAt, new WingetError
    {
        Kind = WingetErrorKind.Cancelled,
        Code = "Cancelled",
        Message = "The operation was cancelled before the installer took control.",
        HResult = exception.HResult
    });

    private static WingetError MapInstallError(InstallResult result)
    {
        var hresult = TryGetInstallHResult(result);
        var (kind, message) = result.Status switch
        {
            InstallResultStatus.BlockedByPolicy => (WingetErrorKind.PolicyBlocked,
                "Installation is blocked by your organization's policy."),
            InstallResultStatus.CatalogError => (WingetErrorKind.Network,
                "The package source could not be reached."),
            InstallResultStatus.DownloadError => (WingetErrorKind.Network,
                "The installer could not be downloaded. Check your connection and try again."),
            InstallResultStatus.NoApplicableInstallers => (WingetErrorKind.InstallerUnavailable,
                "No compatible installer is available for this computer."),
            InstallResultStatus.NoApplicableUpgrade => (WingetErrorKind.InstallerUnavailable,
                "No applicable update is available for this package."),
            InstallResultStatus.PackageAgreementsNotAccepted => (WingetErrorKind.AgreementRequired,
                "The package agreements must be accepted before continuing."),
            InstallResultStatus.InvalidOptions => (WingetErrorKind.ComFailure,
                "The selected install options are not supported by this package."),
            InstallResultStatus.ManifestError => (WingetErrorKind.InstallerUnavailable,
                "The package manifest is invalid or incomplete."),
            InstallResultStatus.InstallError => (ClassifyHResult(hresult),
                "The installer reported an error. No changes were made by Package Pilot."),
            _ => (ClassifyHResult(hresult), "WinGet could not complete the package operation.")
        };

        return new WingetError
        {
            Kind = kind,
            Code = result.Status == InstallResultStatus.InstallError
                ? $"{result.Status}:{result.InstallerErrorCode}"
                : result.Status.ToString(),
            Message = message,
            HResult = hresult
        };
    }

    private static WingetError MapUninstallError(UninstallResult result)
    {
        var hresult = TryGetUninstallHResult(result);
        var (kind, message) = result.Status switch
        {
            UninstallResultStatus.BlockedByPolicy => (WingetErrorKind.PolicyBlocked,
                "Uninstall is blocked by your organization's policy."),
            UninstallResultStatus.CatalogError => (WingetErrorKind.Network,
                "The installed package catalog could not be opened."),
            UninstallResultStatus.InvalidOptions => (WingetErrorKind.ComFailure,
                "The selected uninstall options are not supported by this package."),
            UninstallResultStatus.ManifestError => (WingetErrorKind.InstallerUnavailable,
                "WinGet could not identify an uninstaller for this package."),
            UninstallResultStatus.UninstallError => (ClassifyHResult(hresult),
                "The uninstaller reported an error."),
            _ => (ClassifyHResult(hresult), "WinGet could not uninstall this package.")
        };

        return new WingetError
        {
            Kind = kind,
            Code = result.Status == UninstallResultStatus.UninstallError
                ? $"{result.Status}:{result.UninstallerErrorCode}"
                : result.Status.ToString(),
            Message = message,
            HResult = hresult
        };
    }

    private static WingetError MapFindError(FindPackagesResult result)
    {
        var hresult = TryGetFindHResult(result);
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
            WingetErrorKind.ElevationDenied =>
                "The Windows elevation prompt was cancelled or denied.",
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
            0x800704C7 => WingetErrorKind.ElevationDenied,
            0x80072EE2 or 0x80072EE7 or 0x80072EFD or 0x80072EFE => WingetErrorKind.Network,
            0x800704C6 => WingetErrorKind.Authentication,
            0x800704C8 => WingetErrorKind.Cancelled,
            _ => WingetErrorKind.ComFailure
        };
    }

    private async Task EnsureSupportedAsync(CancellationToken cancellationToken)
    {
        var capabilities = await GetCapabilitiesAsync(cancellationToken);
        if (!capabilities.IsAvailable)
        {
            throw new WingetClientException(new WingetError
            {
                Kind = WingetErrorKind.AppInstallerMissing,
                Code = "WinGetUnavailable",
                Message = capabilities.UnavailableReason
                    ?? "Windows Package Manager is unavailable."
            });
        }

        if (!capabilities.MeetsMinimumContract)
        {
            throw new WingetClientException(new WingetError
            {
                Kind = WingetErrorKind.ContractTooOld,
                Code = $"Contract{capabilities.ContractVersion}",
                Message = "App Installer is too old. Update it to use Package Pilot."
            });
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

    private static uint InferContractVersion(string? version, uint minimum)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return minimum;
        }

        var value = version.Trim().TrimStart('v', 'V');
        return System.Version.TryParse(value, out var parsed) && parsed.Major == 1
            ? Math.Max(minimum, (uint)Math.Max(parsed.Minor, 0))
            : minimum;
    }

    private static bool SupportsContractSix(IReadOnlyList<PackageCatalogReference> catalogs)
    {
        try
        {
            if (catalogs.Count == 0)
            {
                return false;
            }

            var reference = catalogs[0];
            _ = reference.SourceAgreements.Count;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<T> AwaitAsync<T>(
        IAsyncOperation<T> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(operation.Cancel);
        return await operation;
    }

    private static CancellationTokenRegistration RegisterCancelable<TResult, TProgress>(
        IAsyncOperationWithProgress<TResult, TProgress> operation,
        CancellationToken cancellationToken,
        Func<bool> mayCancel) => cancellationToken.Register(() =>
    {
        if (mayCancel())
        {
            operation.Cancel();
        }
    });

    private static void Report(
        IProgress<OperationProgress>? progress,
        Guid operationId,
        PackageOperationState state,
        double? percent,
        string message,
        long? bytesTransferred = null,
        long? bytesTotal = null) => progress?.Report(new OperationProgress
    {
        OperationId = operationId,
        State = state,
        Percent = percent,
        Message = message,
        BytesTransferred = bytesTransferred,
        BytesTotal = bytesTotal,
        Timestamp = DateTimeOffset.UtcNow
    });

    private static PackageOperationState ToInstallState(
        PackageInstallProgressState state,
        bool isUpgrade) => state switch
    {
        PackageInstallProgressState.Queued => PackageOperationState.Queued,
        PackageInstallProgressState.Downloading => PackageOperationState.Downloading,
        PackageInstallProgressState.Installing or PackageInstallProgressState.PostInstall =>
            isUpgrade ? PackageOperationState.Upgrading : PackageOperationState.Installing,
        PackageInstallProgressState.Finished => PackageOperationState.Completed,
        _ => isUpgrade ? PackageOperationState.Upgrading : PackageOperationState.Installing
    };

    private static PackageOperationState ToUninstallState(PackageUninstallProgressState state) =>
        state switch
        {
            PackageUninstallProgressState.Queued => PackageOperationState.Queued,
            PackageUninstallProgressState.Uninstalling or PackageUninstallProgressState.PostUninstall =>
                PackageOperationState.Uninstalling,
            PackageUninstallProgressState.Finished => PackageOperationState.Completed,
            _ => PackageOperationState.Uninstalling
        };

    private static string DescribeState(PackageOperationState state) => state switch
    {
        PackageOperationState.Queued => "Waiting to start…",
        PackageOperationState.Resolving => "Resolving package…",
        PackageOperationState.Downloading => "Downloading installer…",
        PackageOperationState.Installing =>
            "Installer is running. Cancellation can no longer be guaranteed.",
        PackageOperationState.Upgrading =>
            "Update installer is running. Cancellation can no longer be guaranteed.",
        PackageOperationState.Uninstalling =>
            "Uninstaller is running. Cancellation can no longer be guaranteed.",
        PackageOperationState.Completed => "Completed",
        _ => state.ToString()
    };

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

    private static Microsoft.Management.Deployment.PackageMatchField ToDeploymentMatchField(
        CorePackageMatchField field) => field switch
    {
        CorePackageMatchField.Id => Microsoft.Management.Deployment.PackageMatchField.Id,
        CorePackageMatchField.Name => Microsoft.Management.Deployment.PackageMatchField.Name,
        CorePackageMatchField.Moniker => Microsoft.Management.Deployment.PackageMatchField.Moniker,
        CorePackageMatchField.Tag => Microsoft.Management.Deployment.PackageMatchField.Tag,
        CorePackageMatchField.Command => Microsoft.Management.Deployment.PackageMatchField.Command,
        _ => Microsoft.Management.Deployment.PackageMatchField.CatalogDefault
    };

    private static PackageInstallScope ToInstallScope(InstallerScope scope) => scope switch
    {
        InstallerScope.User => PackageInstallScope.User,
        InstallerScope.Machine => PackageInstallScope.System,
        _ => PackageInstallScope.Any
    };

    private static PackageUninstallScope ToUninstallScope(InstallerScope scope) => scope switch
    {
        InstallerScope.User => PackageUninstallScope.User,
        InstallerScope.Machine => PackageUninstallScope.System,
        _ => PackageUninstallScope.Any
    };

    private static InstallerScope ToCoreScope(PackageInstallerScope? scope) => scope switch
    {
        PackageInstallerScope.User => InstallerScope.User,
        PackageInstallerScope.System => InstallerScope.Machine,
        _ => InstallerScope.Unknown
    };

    private static CoreElevationRequirement ToCoreElevation(
        DeploymentElevationRequirement? requirement) => requirement switch
    {
        DeploymentElevationRequirement.ElevationRequired => CoreElevationRequirement.Required,
        DeploymentElevationRequirement.ElevatesSelf => CoreElevationRequirement.MayRequire,
        DeploymentElevationRequirement.ElevationProhibited => CoreElevationRequirement.NotRequired,
        _ => CoreElevationRequirement.Unknown
    };

    private static PackageArchitecture ToCoreArchitecture(ProcessorArchitecture? architecture) =>
        architecture switch
        {
            ProcessorArchitecture.Neutral => PackageArchitecture.Neutral,
            ProcessorArchitecture.X86 => PackageArchitecture.X86,
            ProcessorArchitecture.X64 => PackageArchitecture.X64,
            ProcessorArchitecture.Arm => PackageArchitecture.Arm,
            ProcessorArchitecture.Arm64 => PackageArchitecture.Arm64,
            _ => PackageArchitecture.Unknown
        };

    private static ProcessorArchitecture? ToProcessorArchitecture(PackageArchitecture architecture) =>
        architecture switch
        {
            PackageArchitecture.Neutral => ProcessorArchitecture.Neutral,
            PackageArchitecture.X86 => ProcessorArchitecture.X86,
            PackageArchitecture.X64 => ProcessorArchitecture.X64,
            PackageArchitecture.Arm => ProcessorArchitecture.Arm,
            PackageArchitecture.Arm64 => ProcessorArchitecture.Arm64,
            _ => null
        };

    private static SourceHealth MapFindHealth(FindPackagesResultStatus status) => status switch
    {
        FindPackagesResultStatus.AuthenticationError => SourceHealth.AuthenticationRequired,
        FindPackagesResultStatus.CatalogError => SourceHealth.Unavailable,
        FindPackagesResultStatus.BlockedByPolicy or FindPackagesResultStatus.AccessDenied =>
            SourceHealth.Degraded,
        _ => SourceHealth.Unknown
    };

    private static SourceHealth ToSourceHealth(WingetErrorKind kind) => kind switch
    {
        WingetErrorKind.Authentication => SourceHealth.AuthenticationRequired,
        WingetErrorKind.Network or WingetErrorKind.AppInstallerMissing => SourceHealth.Unavailable,
        WingetErrorKind.PolicyBlocked or WingetErrorKind.AgreementRequired => SourceHealth.Degraded,
        _ => SourceHealth.Unknown
    };

    private static string DescribeFindFailure(FindPackagesResultStatus status, int? hresult)
    {
        var error = MapFindErrorForStatus(status, hresult);
        return error.Message;
    }

    private static WingetError MapFindErrorForStatus(FindPackagesResultStatus status, int? hresult)
    {
        var (kind, message) = status switch
        {
            FindPackagesResultStatus.AuthenticationError => (WingetErrorKind.Authentication,
                "The source requires authentication."),
            FindPackagesResultStatus.AccessDenied => (WingetErrorKind.PolicyBlocked,
                "Access to the source was denied."),
            FindPackagesResultStatus.BlockedByPolicy => (WingetErrorKind.PolicyBlocked,
                "The source is blocked by policy."),
            FindPackagesResultStatus.CatalogError => (WingetErrorKind.Network,
                "The source could not be reached."),
            _ => (ClassifyHResult(hresult), $"The source returned {status}.")
        };
        return new WingetError
        {
            Kind = kind,
            Code = status.ToString(),
            Message = message,
            HResult = hresult
        };
    }

    private static WingetClientException PackageNotFound(PackageKey key) =>
        new(new WingetError
        {
            Kind = WingetErrorKind.PackageNotFound,
            Code = "PackageNotFound",
            Message = $"WinGet could not find {key.Id} in the selected source."
        });

    private static WingetClientException AgreementRequired(
        string message,
        IReadOnlyList<CorePackageAgreement> agreements) => new(new WingetError
    {
        Kind = WingetErrorKind.AgreementRequired,
        Code = "AgreementRequired",
        Message = message
    }, agreements);

    private static int? TryGetConnectHResult(ConnectResult result) =>
        TryGetHResult(() => result.ExtendedErrorCode);

    private static int? TryGetFindHResult(FindPackagesResult result) =>
        TryGetHResult(() => result.ExtendedErrorCode);

    private static int? TryGetInstallHResult(InstallResult result) =>
        TryGetHResult(() => result.ExtendedErrorCode);

    private static int? TryGetUninstallHResult(UninstallResult result) =>
        TryGetHResult(() => result.ExtendedErrorCode);

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

    private static Uri? ToUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static double? NormalizePercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return null;
        }

        var percent = value <= 1 ? value * 100 : value;
        return Math.Clamp(percent, 0, 100);
    }

    private static long? ToNullableLong(ulong value) =>
        value == 0 ? null : value > long.MaxValue ? long.MaxValue : (long)value;

    /// <summary>
    /// C#/WinRT's projected IVectorView supports Count/indexing for the WinGet
    /// out-of-process proxy, but that proxy does not expose the optional iterable
    /// interface. Keep all COM collection traversal on the indexed ABI.
    /// </summary>
    private static IEnumerable<T> EnumerateWinRt<T>(IReadOnlyList<T> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            yield return values[index];
        }
    }

    private static string CreateCorrelationData(Guid operationId) =>
        $"{{\"operationId\":\"{operationId:D}\"}}";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? string.Empty;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record SourceIdentity(string Id, string Name);

    private sealed record ResolvedPackage(CatalogPackage Package, SourceIdentity Source);
}
