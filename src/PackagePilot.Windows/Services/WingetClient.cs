using System.Runtime.InteropServices;
using System.Security.Principal;
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

namespace PackagePilot.Windows.Services;

/// <summary>
/// Out-of-process adapter for the Windows Package Manager deployment API.
/// </summary>
public sealed class WingetClient : IWingetClient, ISourceManagementService
{
    private const string ContractName =
        "Microsoft.Management.Deployment.WindowsPackageManagerContract";

    private readonly object _capabilitiesLock = new();
    private readonly object _managerLock = new();
    private readonly WindowsUpdateDiscoveryClient _updateDiscoveryClient = new();
    private WingetCapabilities? _supportedCapabilities;
    private PackageManager? _manager;

    public Task<WingetCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_capabilitiesLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_supportedCapabilities is not null)
            {
                return Task.FromResult(_supportedCapabilities);
            }

            var capabilities = ProbeCapabilities();
            // Keep failures retryable in case App Installer is repaired while the app is open.
            if (capabilities.MeetsMinimumContract)
            {
                _supportedCapabilities = capabilities;
            }

            return Task.FromResult(capabilities);
        }
    }

    private WingetCapabilities ProbeCapabilities()
    {
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

            return new WingetCapabilities
            {
                IsAvailable = true,
                ContractVersion = contractVersion,
                Version = version,
                UnavailableReason = contractVersion < WingetCapabilities.RequiredContractVersion
                    ? "App Installer is too old. Package Pilot requires WinGet API contract 6 or newer."
                    : null
            };
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var error = FromException(exception);
            return new WingetCapabilities
            {
                IsAvailable = false,
                ContractVersion = contractVersion,
                UnavailableReason = error.Message
            };
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
                var result = await ConnectAsync(reference, cancellationToken);
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

    public async Task<SourceManagementCapabilities> GetSourceManagementCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        var capabilities = await GetCapabilitiesAsync(cancellationToken);
        return CreateSourceManagementCapabilities(capabilities, IsCurrentProcessElevated());
    }

    public async Task<IReadOnlyList<PackageSourceInfo>> GetSourceDetailsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSupportedAsync(cancellationToken);

        var sources = new List<PackageSourceInfo>();
        foreach (var reference in EnumerateWinRt(GetManager().GetPackageCatalogs()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = CreateSourceInfo(reference);

            try
            {
                var result = await ConnectAsync(reference, cancellationToken);
                source = source with
                {
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
                    }
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                var error = FromException(exception);
                source = source with
                {
                    Health = ToSourceHealth(error.Kind),
                    Message = error.Message
                };
            }

            sources.Add(source);
        }

        return sources
            .OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<SourceOperationResult> RefreshSourceAsync(
        string sourceName,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        const SourceOperationKind kind = SourceOperationKind.Refresh;
        var normalizedName = sourceName?.Trim() ?? string.Empty;

        try
        {
            var validation = SourceRequestValidator.ValidateSourceName(sourceName);
            if (!validation.IsValid)
            {
                return InvalidSourceRequest(operationId, kind, normalizedName, validation);
            }

            var capabilities = await GetSourceManagementCapabilitiesAsync(cancellationToken);
            if (!capabilities.SupportsRefresh)
            {
                return UnsupportedSourceOperation(operationId, kind, normalizedName,
                    SourceManagementCapabilities.RefreshAndMutationContractVersion,
                    capabilities);
            }

            var source = FindConfiguredSource(normalizedName);
            if (source is null)
            {
                return SourceNotFound(operationId, kind, normalizedName);
            }

            return await ExecuteSourceOperationAsync(
                operationId,
                kind,
                source.Name,
                source.Reference.RefreshPackageCatalogAsync,
                result => ToNativeSourceResult(result.Status, TryGetSourceHResult(result)),
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            return CancelledSourceOperation(operationId, kind, normalizedName, exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return FailedSourceOperation(operationId, kind, normalizedName, exception);
        }
    }

    public async Task<SourceOperationResult> AddSourceAsync(
        AddPackageSourceRequest request,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationId = Guid.NewGuid();
        const SourceOperationKind kind = SourceOperationKind.Add;
        var sourceName = request.Name?.Trim() ?? string.Empty;

        try
        {
            var validation = SourceRequestValidator.Validate(request);
            if (!validation.IsValid)
            {
                return InvalidSourceRequest(operationId, kind, sourceName, validation);
            }

            var capabilities = await GetSourceManagementCapabilitiesAsync(cancellationToken);
            if (!capabilities.SupportsAdd)
            {
                return UnsupportedSourceOperation(operationId, kind, sourceName,
                    SourceManagementCapabilities.RefreshAndMutationContractVersion,
                    capabilities);
            }

            if (!capabilities.IsCurrentProcessElevated)
            {
                return ElevationRequired(operationId, kind, sourceName);
            }

            var options = new AddPackageCatalogOptions
            {
                Name = sourceName,
                SourceUri = request.Location.Trim(),
                Type = SourceRequestValidator.ToDeploymentType(request.Type),
                Explicit = request.IsExplicit,
                TrustLevel = request.TrustLevel == PackageSourceTrustLevel.Trusted
                    ? PackageCatalogTrustLevel.Trusted
                    : PackageCatalogTrustLevel.None
            };

            // Advanced headers are intentionally not part of the public request model so they
            // cannot be persisted or emitted by structured logging.
            return await ExecuteSourceOperationAsync(
                operationId,
                kind,
                sourceName,
                () => GetManager().AddPackageCatalogAsync(options),
                result => ToNativeSourceResult(result.Status, TryGetSourceHResult(result)),
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            return CancelledSourceOperation(operationId, kind, sourceName, exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return FailedSourceOperation(operationId, kind, sourceName, exception);
        }
    }

    public async Task<SourceOperationResult> RemoveSourceAsync(
        string sourceName,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        const SourceOperationKind kind = SourceOperationKind.Remove;
        var normalizedName = sourceName?.Trim() ?? string.Empty;

        try
        {
            var validation = SourceRequestValidator.ValidateSourceName(sourceName);
            if (!validation.IsValid)
            {
                return InvalidSourceRequest(operationId, kind, normalizedName, validation);
            }

            var capabilities = await GetSourceManagementCapabilitiesAsync(cancellationToken);
            if (!capabilities.SupportsRemove)
            {
                return UnsupportedSourceOperation(operationId, kind, normalizedName,
                    SourceManagementCapabilities.RefreshAndMutationContractVersion,
                    capabilities);
            }

            var source = FindConfiguredSource(normalizedName);
            if (source is null)
            {
                return SourceNotFound(operationId, kind, normalizedName);
            }

            if (source.Origin == PackageSourceOrigin.Predefined)
            {
                return SourceOperationNotAllowed(operationId, kind, source.Name,
                    "Predefined WinGet sources cannot be removed.");
            }

            if (!capabilities.IsCurrentProcessElevated)
            {
                return ElevationRequired(operationId, kind, source.Name);
            }

            var options = new RemovePackageCatalogOptions
            {
                Name = source.Name,
                PreserveData = false
            };
            return await ExecuteSourceOperationAsync(
                operationId,
                kind,
                source.Name,
                () => GetManager().RemovePackageCatalogAsync(options),
                result => ToNativeSourceResult(result.Status, TryGetSourceHResult(result)),
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            return CancelledSourceOperation(operationId, kind, normalizedName, exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return FailedSourceOperation(operationId, kind, normalizedName, exception);
        }
    }

    public async Task<SourceOperationResult> ResetSourceAsync(
        ResetPackageSourceRequest request,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationId = Guid.NewGuid();
        const SourceOperationKind kind = SourceOperationKind.Reset;
        var sourceName = request.SourceName?.Trim() ?? string.Empty;

        try
        {
            var validation = SourceRequestValidator.Validate(request);
            if (!validation.IsValid)
            {
                return InvalidSourceRequest(operationId, kind, sourceName, validation);
            }

            var capabilities = await GetSourceManagementCapabilitiesAsync(cancellationToken);
            if (!capabilities.SupportsResetOne)
            {
                return UnsupportedSourceOperation(operationId, kind, sourceName,
                    SourceManagementCapabilities.RefreshAndMutationContractVersion,
                    capabilities);
            }

            var source = FindConfiguredSource(sourceName);
            if (source is null)
            {
                return SourceNotFound(operationId, kind, sourceName);
            }

            if (source.Origin != PackageSourceOrigin.Predefined)
            {
                return SourceOperationNotAllowed(operationId, kind, source.Name,
                    "Only one named predefined WinGet source can be reset by this operation.");
            }

            if (!capabilities.IsCurrentProcessElevated)
            {
                return ElevationRequired(operationId, kind, source.Name);
            }

            var options = new RemovePackageCatalogOptions
            {
                Name = source.Name,
                PreserveData = true
            };
            return await ExecuteSourceOperationAsync(
                operationId,
                kind,
                source.Name,
                () => GetManager().RemovePackageCatalogAsync(options),
                result => ToNativeSourceResult(result.Status, TryGetSourceHResult(result)),
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            return CancelledSourceOperation(operationId, kind, sourceName, exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return FailedSourceOperation(operationId, kind, sourceName, exception);
        }
    }

    public async Task<SourceOperationResult> SetSourceExplicitAsync(
        string sourceName,
        bool isExplicit,
        IProgress<SourceOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        const SourceOperationKind kind = SourceOperationKind.EditExplicit;
        var normalizedName = sourceName?.Trim() ?? string.Empty;

        try
        {
            var validation = SourceRequestValidator.ValidateSourceName(sourceName);
            if (!validation.IsValid)
            {
                return InvalidSourceRequest(operationId, kind, normalizedName, validation);
            }

            var capabilities = await GetSourceManagementCapabilitiesAsync(cancellationToken);
            if (!capabilities.SupportsExplicitEdit)
            {
                return UnsupportedSourceOperation(operationId, kind, normalizedName,
                    SourceManagementCapabilities.ExplicitEditContractVersion,
                    capabilities);
            }

            var source = FindConfiguredSource(normalizedName);
            if (source is null)
            {
                return SourceNotFound(operationId, kind, normalizedName);
            }

            if (source.Origin == PackageSourceOrigin.Predefined)
            {
                return SourceOperationNotAllowed(operationId, kind, source.Name,
                    "Predefined WinGet sources cannot be edited.");
            }

            if (!capabilities.IsCurrentProcessElevated)
            {
                return ElevationRequired(operationId, kind, source.Name);
            }

            ReportSourceProgress(progress, operationId, kind, source.Name, 0,
                "Editing source settings…");
            cancellationToken.ThrowIfCancellationRequested();
            var options = new EditPackageCatalogOptions
            {
                Name = source.Name,
                Explicit = isExplicit
            };
            var nativeResult = await Task.Run(
                () => GetManager().EditPackageCatalog(options),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var mapped = ToNativeSourceResult(
                nativeResult.Status,
                TryGetSourceHResult(nativeResult));
            var result = CreateSourceOperationResult(
                operationId,
                kind,
                source.Name,
                mapped);
            ReportSourceProgress(progress, operationId, kind, source.Name,
                result.IsSuccess ? 100 : null,
                result.Message);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            return CancelledSourceOperation(operationId, kind, normalizedName, exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return FailedSourceOperation(operationId, kind, normalizedName, exception);
        }
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
                var currentSourceAgreements = GetSourceAgreements(reference, identity);
                var currentFingerprint = SourceAgreementSnapshot.Create(
                    identity.Id,
                    currentSourceAgreements).Fingerprint;
                // This flag is only true after the UI has displayed the source terms
                // and captured explicit user consent.
                if (query.AcceptedSourceAgreementFingerprints.TryGetValue(
                        identity.Id,
                        out var acceptedFingerprint)
                    && string.Equals(
                        currentFingerprint,
                        acceptedFingerprint,
                        StringComparison.Ordinal))
                {
                    reference.AcceptSourceAgreements = true;
                }
                var connectResult = await ConnectAsync(reference, cancellationToken);
                var sourceStatus = ToSourceStatus(
                    identity,
                    connectResult,
                    connectResult.Status == ConnectResultStatus.SourceAgreementsNotAccepted
                        ? currentSourceAgreements
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

    public Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesAsync(
        CancellationToken cancellationToken = default) =>
        _updateDiscoveryClient.GetAvailableUpdatesAsync(cancellationToken);

    internal Task<IReadOnlyList<PackageSummary>> GetAvailableUpdatesPerSourceAsync(
        CancellationToken cancellationToken = default) =>
        _updateDiscoveryClient.GetAvailableUpdatesPerSourceAsync(cancellationToken);

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

            var sourceAgreements = GetSourceAgreements(reference, source);
            var currentFingerprint = SourceAgreementSnapshot.Create(
                source.Id,
                sourceAgreements).Fingerprint;
            if (preferences.AcceptSourceAgreements
                && string.Equals(
                    preferences.AcceptedSourceAgreementFingerprint,
                    currentFingerprint,
                    StringComparison.Ordinal))
            {
                reference.AcceptSourceAgreements = true;
            }
            var connectResult = await ConnectAsync(reference, cancellationToken);
            if (connectResult.Status == ConnectResultStatus.SourceAgreementsNotAccepted)
            {
                throw AgreementRequired(
                    $"The {source.Name} source requires agreement acceptance before it can be used.",
                    sourceAgreements);
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
        var source = string.IsNullOrWhiteSpace(fallbackSource.Id)
            ? TryGetVersionSource(available) ?? fallbackSource
            : fallbackSource;
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
        return ToUri(icon?.Url);
    }

    private async Task<PackageCatalog> ConnectOrThrowAsync(
        PackageCatalogReference reference,
        CancellationToken cancellationToken)
    {
        var source = GetSourceIdentity(reference);
        var result = await ConnectAsync(reference, cancellationToken);
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
            Agreements = agreements,
            AgreementFingerprint = SourceAgreementSnapshot.Create(identity.Id, agreements).Fingerprint
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

    internal static SourceManagementCapabilities CreateSourceManagementCapabilities(
        WingetCapabilities capabilities,
        bool isCurrentProcessElevated)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return new SourceManagementCapabilities
        {
            IsAvailable = capabilities.IsAvailable,
            ContractVersion = capabilities.ContractVersion,
            IsCurrentProcessElevated = isCurrentProcessElevated,
            UnavailableReason = capabilities.UnavailableReason
        };
    }

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }

    private PackageSourceInfo CreateSourceInfo(PackageCatalogReference reference)
    {
        var identity = GetSourceIdentity(reference);
        var agreements = GetSourceAgreements(reference, identity);

        try
        {
            var info = reference.Info;
            var typeName = NullIfWhiteSpace(info.Type) ?? string.Empty;
            return new PackageSourceInfo
            {
                Id = identity.Id,
                Name = identity.Name,
                Type = SourceRequestValidator.FromDeploymentType(typeName),
                TypeName = typeName,
                Argument = NullIfWhiteSpace(info.Argument) ?? string.Empty,
                LastUpdatedAt = TryGetLastUpdatedAt(info),
                Origin = ToCoreSourceOrigin(info.Origin),
                TrustLevel = ToCoreSourceTrustLevel(info.TrustLevel),
                IsExplicit = TryGetSourceExplicit(info),
                AgreementSnapshot = SourceAgreementSnapshot.Create(identity.Id, agreements)
            };
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var error = FromException(exception);
            return new PackageSourceInfo
            {
                Id = identity.Id,
                Name = identity.Name,
                Health = ToSourceHealth(error.Kind),
                Message = error.Message,
                AgreementSnapshot = SourceAgreementSnapshot.Create(identity.Id, agreements)
            };
        }
    }

    private ManagedSource? FindConfiguredSource(string sourceName)
    {
        foreach (var reference in EnumerateWinRt(GetManager().GetPackageCatalogs()))
        {
            var info = CreateSourceInfo(reference);
            if (string.Equals(info.Name, sourceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(info.Id, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                return new ManagedSource(reference, info.Name, info.Origin);
            }
        }

        return null;
    }

    private static DateTimeOffset? TryGetLastUpdatedAt(PackageCatalogInfo info)
    {
        try
        {
            var value = info.LastUpdateTime;
            return value == default ? null : value;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return null;
        }
    }

    private static bool TryGetSourceExplicit(PackageCatalogInfo info)
    {
        try
        {
            return info.Explicit;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return false;
        }
    }

    private static PackageSourceOrigin ToCoreSourceOrigin(PackageCatalogOrigin origin) =>
        origin switch
        {
            PackageCatalogOrigin.Predefined => PackageSourceOrigin.Predefined,
            PackageCatalogOrigin.User => PackageSourceOrigin.User,
            _ => PackageSourceOrigin.Unknown
        };

    private static PackageSourceTrustLevel ToCoreSourceTrustLevel(
        PackageCatalogTrustLevel trustLevel) => trustLevel switch
    {
        PackageCatalogTrustLevel.Trusted => PackageSourceTrustLevel.Trusted,
        _ => PackageSourceTrustLevel.None
    };

    private async Task<SourceOperationResult> ExecuteSourceOperationAsync<TResult>(
        Guid operationId,
        SourceOperationKind kind,
        string sourceName,
        Func<IAsyncOperationWithProgress<TResult, double>> startOperation,
        Func<TResult, NativeSourceResult> mapResult,
        IProgress<SourceOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ReportSourceProgress(progress, operationId, kind, sourceName, 0,
            DescribeSourceOperation(kind));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = await Task.Run(startOperation, cancellationToken);
            operation.Progress = (_, value) => ReportSourceProgress(
                progress,
                operationId,
                kind,
                sourceName,
                NormalizeSourcePercent(value),
                DescribeSourceOperation(kind));
            using var registration = cancellationToken.Register(operation.Cancel);
            var nativeResult = await operation;
            cancellationToken.ThrowIfCancellationRequested();

            var result = CreateSourceOperationResult(
                operationId,
                kind,
                sourceName,
                mapResult(nativeResult));
            ReportSourceProgress(
                progress,
                operationId,
                kind,
                sourceName,
                result.IsSuccess ? 100 : null,
                result.Message);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            return CancelledSourceOperation(operationId, kind, sourceName, exception);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return FailedSourceOperation(operationId, kind, sourceName, exception);
        }
    }

    private static void ReportSourceProgress(
        IProgress<SourceOperationProgress>? progress,
        Guid operationId,
        SourceOperationKind kind,
        string sourceName,
        double? percent,
        string message) => progress?.Report(new SourceOperationProgress
    {
        OperationId = operationId,
        Kind = kind,
        SourceName = sourceName,
        Percent = percent,
        Message = message,
        Timestamp = DateTimeOffset.UtcNow
    });

    internal static double? NormalizeSourcePercent(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) || value < 0
            ? null
            : Math.Clamp(value, 0, 100);

    private static string DescribeSourceOperation(SourceOperationKind kind) => kind switch
    {
        SourceOperationKind.Refresh => "Refreshing source…",
        SourceOperationKind.Add => "Adding source…",
        SourceOperationKind.Remove => "Removing source…",
        SourceOperationKind.Reset => "Resetting source…",
        SourceOperationKind.EditExplicit => "Editing source settings…",
        _ => "Managing source…"
    };

    private static string DescribeSourceSuccess(SourceOperationKind kind, string sourceName) =>
        kind switch
        {
            SourceOperationKind.Refresh => $"Refreshed {sourceName}.",
            SourceOperationKind.Add => $"Added {sourceName}.",
            SourceOperationKind.Remove => $"Removed {sourceName}.",
            SourceOperationKind.Reset => $"Reset {sourceName}.",
            SourceOperationKind.EditExplicit => $"Updated {sourceName}.",
            _ => $"Updated {sourceName}."
        };

    private static SourceOperationResult CreateSourceOperationResult(
        Guid operationId,
        SourceOperationKind kind,
        string sourceName,
        NativeSourceResult nativeResult) => new()
    {
        OperationId = operationId,
        Kind = kind,
        SourceName = sourceName,
        Status = nativeResult.Status,
        Message = nativeResult.Status == SourceOperationStatus.Succeeded
            ? DescribeSourceSuccess(kind, sourceName)
            : nativeResult.Message,
        HResult = nativeResult.HResult
    };

    private static SourceOperationResult InvalidSourceRequest(
        Guid operationId,
        SourceOperationKind kind,
        string sourceName,
        SourceRequestValidationResult validation) => new()
    {
        OperationId = operationId,
        Kind = kind,
        SourceName = sourceName,
        Status = SourceOperationStatus.InvalidRequest,
        Message = string.Join(" ", validation.Errors)
    };

    private static SourceOperationResult UnsupportedSourceOperation(
        Guid operationId,
        SourceOperationKind kind,
        string sourceName,
        uint requiredContract,
        SourceManagementCapabilities capabilities) => new()
    {
        OperationId = operationId,
        Kind = kind,
        SourceName = sourceName,
        Status = capabilities.IsAvailable
            ? SourceOperationStatus.Unsupported
            : SourceOperationStatus.Unavailable,
        Message = capabilities.IsAvailable
            ? $"This source operation requires WinGet API contract {requiredContract} or newer."
            : capabilities.UnavailableReason ?? "Windows Package Manager is unavailable."
    };

    private static SourceOperationResult ElevationRequired(
        Guid operationId,
        SourceOperationKind kind,
        string sourceName) => new()
    {
        OperationId = operationId,
        Kind = kind,
        SourceName = sourceName,
        Status = SourceOperationStatus.AccessDenied,
        Message = "Changing WinGet sources requires administrator approval."
    };

    private static SourceOperationResult SourceNotFound(
        Guid operationId,
        SourceOperationKind kind,
        string sourceName) => new()
    {
        OperationId = operationId,
        Kind = kind,
        SourceName = sourceName,
        Status = SourceOperationStatus.NotFound,
        Message = $"WinGet source '{sourceName}' was not found."
    };

    private static SourceOperationResult SourceOperationNotAllowed(
        Guid operationId,
        SourceOperationKind kind,
        string sourceName,
        string message) => new()
    {
        OperationId = operationId,
        Kind = kind,
        SourceName = sourceName,
        Status = SourceOperationStatus.NotAllowed,
        Message = message
    };

    private static SourceOperationResult CancelledSourceOperation(
        Guid operationId,
        SourceOperationKind kind,
        string sourceName,
        Exception exception) => new()
    {
        OperationId = operationId,
        Kind = kind,
        SourceName = sourceName,
        Status = SourceOperationStatus.Cancelled,
        Message = "The source operation was cancelled.",
        HResult = exception.HResult
    };

    private static SourceOperationResult FailedSourceOperation(
        Guid operationId,
        SourceOperationKind kind,
        string sourceName,
        Exception exception)
    {
        var error = FromException(exception);
        var status = unchecked((uint)exception.HResult) switch
        {
            0x80070005 or 0x800702E4 or 0x800704C7 => SourceOperationStatus.AccessDenied,
            _ => error.Kind switch
            {
                WingetErrorKind.PolicyBlocked => SourceOperationStatus.BlockedByPolicy,
                WingetErrorKind.Authentication => SourceOperationStatus.AuthenticationRequired,
                WingetErrorKind.Network or WingetErrorKind.AppInstallerMissing =>
                    SourceOperationStatus.Unavailable,
                WingetErrorKind.Cancelled => SourceOperationStatus.Cancelled,
                _ => SourceOperationStatus.Failed
            }
        };
        return new SourceOperationResult
        {
            OperationId = operationId,
            Kind = kind,
            SourceName = sourceName,
            Status = status,
            Message = status switch
            {
                SourceOperationStatus.AccessDenied =>
                    "Changing WinGet sources requires administrator approval.",
                SourceOperationStatus.BlockedByPolicy =>
                    "This source is managed or blocked by your organization.",
                SourceOperationStatus.AuthenticationRequired =>
                    "The source requires authentication.",
                SourceOperationStatus.Unavailable =>
                    "The source could not be reached.",
                SourceOperationStatus.Cancelled =>
                    "The source operation was cancelled.",
                _ => error.Message
            },
            HResult = exception.HResult
        };
    }

    internal static SourceOperationStatus MapSourceStatus(
        RefreshPackageCatalogStatus status) => status switch
    {
        RefreshPackageCatalogStatus.Ok => SourceOperationStatus.Succeeded,
        RefreshPackageCatalogStatus.GroupPolicyError => SourceOperationStatus.BlockedByPolicy,
        RefreshPackageCatalogStatus.CatalogError => SourceOperationStatus.Unavailable,
        _ => SourceOperationStatus.Failed
    };

    internal static SourceOperationStatus MapSourceStatus(AddPackageCatalogStatus status) =>
        status switch
        {
            AddPackageCatalogStatus.Ok => SourceOperationStatus.Succeeded,
            AddPackageCatalogStatus.GroupPolicyError => SourceOperationStatus.BlockedByPolicy,
            AddPackageCatalogStatus.CatalogError => SourceOperationStatus.Unavailable,
            AddPackageCatalogStatus.InvalidOptions => SourceOperationStatus.InvalidRequest,
            AddPackageCatalogStatus.AccessDenied => SourceOperationStatus.AccessDenied,
            AddPackageCatalogStatus.AuthenticationError =>
                SourceOperationStatus.AuthenticationRequired,
            _ => SourceOperationStatus.Failed
        };

    internal static SourceOperationStatus MapSourceStatus(RemovePackageCatalogStatus status) =>
        status switch
        {
            RemovePackageCatalogStatus.Ok => SourceOperationStatus.Succeeded,
            RemovePackageCatalogStatus.GroupPolicyError => SourceOperationStatus.BlockedByPolicy,
            RemovePackageCatalogStatus.CatalogError => SourceOperationStatus.Unavailable,
            RemovePackageCatalogStatus.AccessDenied => SourceOperationStatus.AccessDenied,
            RemovePackageCatalogStatus.InvalidOptions => SourceOperationStatus.InvalidRequest,
            _ => SourceOperationStatus.Failed
        };

    internal static SourceOperationStatus MapSourceStatus(EditPackageCatalogStatus status) =>
        status switch
        {
            EditPackageCatalogStatus.Ok => SourceOperationStatus.Succeeded,
            EditPackageCatalogStatus.GroupPolicyError => SourceOperationStatus.BlockedByPolicy,
            EditPackageCatalogStatus.CatalogError => SourceOperationStatus.Unavailable,
            EditPackageCatalogStatus.AccessDenied => SourceOperationStatus.AccessDenied,
            EditPackageCatalogStatus.InvalidOptions => SourceOperationStatus.InvalidRequest,
            _ => SourceOperationStatus.Failed
        };

    private static NativeSourceResult ToNativeSourceResult(
        RefreshPackageCatalogStatus status,
        int? hresult) => new(
            MapSourceStatus(status),
            DescribeSourceFailure(MapSourceStatus(status)),
            hresult);

    private static NativeSourceResult ToNativeSourceResult(
        AddPackageCatalogStatus status,
        int? hresult) => new(
            MapSourceStatus(status),
            DescribeSourceFailure(MapSourceStatus(status)),
            hresult);

    private static NativeSourceResult ToNativeSourceResult(
        RemovePackageCatalogStatus status,
        int? hresult) => new(
            MapSourceStatus(status),
            DescribeSourceFailure(MapSourceStatus(status)),
            hresult);

    private static NativeSourceResult ToNativeSourceResult(
        EditPackageCatalogStatus status,
        int? hresult) => new(
            MapSourceStatus(status),
            DescribeSourceFailure(MapSourceStatus(status)),
            hresult);

    private static string DescribeSourceFailure(SourceOperationStatus status) => status switch
    {
        SourceOperationStatus.BlockedByPolicy =>
            "This source is managed or blocked by your organization.",
        SourceOperationStatus.Unavailable =>
            "The source could not be reached. Check its address and your network connection.",
        SourceOperationStatus.InvalidRequest =>
            "WinGet rejected the source settings.",
        SourceOperationStatus.AccessDenied =>
            "Changing WinGet sources requires administrator approval.",
        SourceOperationStatus.AuthenticationRequired =>
            "The source requires authentication.",
        SourceOperationStatus.Failed =>
            "Windows Package Manager could not complete the source operation.",
        _ => string.Empty
    };

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

    private static async Task<ConnectResult> ConnectAsync(
        PackageCatalogReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // WinGet currently performs catalog opening before ConnectAsync returns.
        // Start that native work on the thread pool so remote sources cannot block
        // the WinUI dispatcher while the operation is being created.
        var operation = await Task.Run(reference.ConnectAsync, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await AwaitAsync(operation, cancellationToken);
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

    private static int? TryGetSourceHResult(RefreshPackageCatalogResult result) =>
        TryGetHResult(() => result.ExtendedErrorCode);

    private static int? TryGetSourceHResult(AddPackageCatalogResult result) =>
        TryGetHResult(() => result.ExtendedErrorCode);

    private static int? TryGetSourceHResult(RemovePackageCatalogResult result) =>
        TryGetHResult(() => result.ExtendedErrorCode);

    private static int? TryGetSourceHResult(EditPackageCatalogResult result) =>
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

    private sealed record ManagedSource(
        PackageCatalogReference Reference,
        string Name,
        PackageSourceOrigin Origin);

    private sealed record NativeSourceResult(
        SourceOperationStatus Status,
        string Message,
        int? HResult);

    private sealed record ResolvedPackage(CatalogPackage Package, SourceIdentity Source);
}
