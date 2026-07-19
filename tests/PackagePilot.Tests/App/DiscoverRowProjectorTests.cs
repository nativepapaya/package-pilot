using PackagePilot.App.Views;
using PackagePilot.Core.Models;

namespace PackagePilot.Tests.App;

public sealed class DiscoverRowProjectorTests
{
    [Fact]
    public void ExactInstalledPackage_ShowsPositiveInstalledStateWithoutMutationAction()
    {
        var search = Package("Contoso.App", "source-a", available: "2.0");
        var installed = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.Installed,
            installed: "1.4");

        var row = Apply(search, [search], installed: [installed]);

        Assert.Equal("Installed", row.Status);
        Assert.Equal("Installed", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
        Assert.Null(row.RequestedOperationKind);
        Assert.Equal("1.4", row.InstalledVersion);
        Assert.Empty(row.AvailableVersion);
        Assert.True(row.IsPositiveState);
        Assert.False(string.IsNullOrWhiteSpace(row.StateGlyph));
    }

    [Fact]
    public void ExactAvailableUpdate_TakesPrecedenceOverInstalledState()
    {
        var search = Package("Contoso.App", "source-a", available: "2.0");
        var installed = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.Installed,
            installed: "1.4");
        var update = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.UpdateAvailable,
            installed: "1.4",
            available: "2.0");

        var row = Apply(search, [search], installed: [installed], updates: [update]);

        Assert.Equal("Update available", row.Status);
        Assert.Equal("Update", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
        Assert.Equal(PackageOperationKind.Upgrade, row.RequestedOperationKind);
        Assert.Equal("1.4", row.InstalledVersion);
        Assert.Equal("2.0", row.AvailableVersion);
        Assert.False(row.IsPositiveState);
    }

    [Fact]
    public void ExactInstalledAggregate_OffersViewInstalledWithoutMutation()
    {
        var search = Package("Contoso.App", "source-a");
        var installed = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.Installed,
            installed: "1.0");
        var installedApp = new InstalledApp
        {
            Id = "installed-app-1",
            Name = "Contoso App",
            Installations =
            [
                new Installation
                {
                    Provider = InstalledAppProviderKind.Winget,
                    WingetPackage = search.Key
                }
            ]
        };
        var index = DiscoverRowProjector.CreateIndex(
            [search],
            [installed],
            [],
            new OperationQueueSnapshot(),
            [installedApp]);

        var row = DiscoverRowProjector.Apply(Row(search), search, index);

        Assert.Equal("View installed", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
        Assert.Null(row.RequestedOperationKind);
        Assert.Equal(installedApp.Id, row.InstalledAppId);
    }

    [Fact]
    public void InstalledAggregateFromDifferentSource_DoesNotOfferViewInstalled()
    {
        var search = Package("Contoso.App", "source-b");
        var local = Package(
            "Contoso.App",
            "source-b",
            status: PackageStatus.Installed,
            installed: "1.0");
        var installedApp = new InstalledApp
        {
            Id = "installed-app-1",
            Installations =
            [
                new Installation
                {
                    Provider = InstalledAppProviderKind.Winget,
                    WingetPackage = new PackageKey("Contoso.App", "source-a")
                }
            ]
        };
        var index = DiscoverRowProjector.CreateIndex(
            [search],
            [local],
            [],
            new OperationQueueSnapshot(),
            [installedApp]);

        var row = DiscoverRowProjector.Apply(Row(search), search, index);

        Assert.Equal("Installed", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
        Assert.Null(row.InstalledAppId);
    }

    [Fact]
    public void SameNameAndIdFromDifferentAttributedSource_DoesNotJoin()
    {
        var search = Package("Contoso.App", "source-b", name: "Contoso App");
        var otherSource = Package(
            "Contoso.App",
            "source-a",
            name: "Contoso App",
            status: PackageStatus.Installed,
            installed: "1.0");

        var row = Apply(search, [search], installed: [otherSource]);

        Assert.Equal("Available", row.Status);
        Assert.Equal("Install", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
        Assert.Equal(PackageOperationKind.Install, row.RequestedOperationKind);
    }

    [Fact]
    public void UnattributedLocalCatalogId_JoinsOnlyWhenSearchSourceIsUnambiguous()
    {
        var sourceA = Package("Contoso.App", "source-a");
        var sourceB = Package("Contoso.App", "source-b");
        var local = Package(
            "Contoso.App",
            "installed",
            status: PackageStatus.Installed,
            installed: "1.0");

        var unique = Apply(sourceA, [sourceA], installed: [local]);
        var ambiguousA = Apply(sourceA, [sourceA, sourceB], installed: [local]);
        var ambiguousB = Apply(sourceB, [sourceA, sourceB], installed: [local]);

        Assert.Equal("Installed", unique.Status);
        Assert.Equal("Available", ambiguousA.Status);
        Assert.Equal("Available", ambiguousB.Status);
    }

    [Fact]
    public void PredefinedInstalledCatalogId_ShowsInstalledForUnambiguousSearchResult()
    {
        var search = Package("Contoso.App", "source-a", available: "2.0");
        var installed = Package(
            "Contoso.App",
            "*PredefinedInstalledSource",
            status: PackageStatus.Installed,
            installed: "1.4");

        var row = Apply(search, [search], installed: [installed]);

        Assert.Equal("Installed", row.Status);
        Assert.Equal("Installed", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
        Assert.Null(row.RequestedOperationKind);
        Assert.Equal("1.4", row.InstalledVersion);
    }

    [Fact]
    public void PredefinedInstalledAggregate_OffersViewInstalledForUnambiguousSearchResult()
    {
        var search = Package("Contoso.App", "source-a");
        var installed = Package(
            "Contoso.App",
            "*PredefinedInstalledSource",
            status: PackageStatus.Installed,
            installed: "1.0");
        var installedApp = new InstalledApp
        {
            Id = "installed-app-1",
            Name = "Contoso App",
            Installations =
            [
                new Installation
                {
                    Provider = InstalledAppProviderKind.Winget,
                    WingetPackage = installed.Key
                }
            ]
        };
        var index = DiscoverRowProjector.CreateIndex(
            [search],
            [installed],
            [],
            new OperationQueueSnapshot(),
            [installedApp]);

        var row = DiscoverRowProjector.Apply(Row(search), search, index);

        Assert.Equal("Installed", row.Status);
        Assert.Equal("View installed", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
        Assert.Null(row.RequestedOperationKind);
        Assert.Equal(installedApp.Id, row.InstalledAppId);
    }

    [Fact]
    public void PredefinedInstalledCatalogId_DoesNotGuessAcrossDuplicateSearchSources()
    {
        var sourceA = Package("Contoso.App", "source-a");
        var sourceB = Package("Contoso.App", "source-b");
        var installed = Package(
            "Contoso.App",
            "*PredefinedInstalledSource",
            status: PackageStatus.Installed,
            installed: "1.0");

        var rowA = Apply(sourceA, [sourceA, sourceB], installed: [installed]);
        var rowB = Apply(sourceB, [sourceA, sourceB], installed: [installed]);

        Assert.Equal("Available", rowA.Status);
        Assert.Equal("Install", rowA.ActionLabel);
        Assert.Equal("Available", rowB.Status);
        Assert.Equal("Install", rowB.ActionLabel);
    }

    [Theory]
    [InlineData(PackageOperationState.Queued, "Queued", "Update queued - waiting to start")]
    [InlineData(PackageOperationState.Resolving, "Preparing", "Preparing update...")]
    [InlineData(PackageOperationState.Downloading, "Downloading", "Downloading update...")]
    [InlineData(PackageOperationState.Upgrading, "Updating", "Installing update...")]
    public void ExactQueuedOrRunningUpdate_DisablesActionAndShowsProgress(
        PackageOperationState state,
        string expectedAction,
        string expectedStatus)
    {
        var search = Package("Contoso.App", "source-a");
        var update = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.UpdateAvailable,
            installed: "1.0",
            available: "2.0");
        var active = Entry(search.Key, PackageOperationKind.Upgrade, state);
        var queue = state == PackageOperationState.Queued
            ? new OperationQueueSnapshot { Pending = [active] }
            : new OperationQueueSnapshot { Current = active };

        var row = Apply(search, [search], updates: [update], queue: queue);

        Assert.Equal(state, row.OperationState);
        Assert.Equal(expectedAction, row.ActionLabel);
        Assert.Equal(expectedStatus, row.Status);
        Assert.False(row.IsActionEnabled);
        Assert.False(row.IsPositiveState);
    }

    [Fact]
    public void ActiveOperationForSameIdFromOtherSource_DoesNotAffectRow()
    {
        var search = Package("Contoso.App", "source-b");
        var otherSourceOperation = Entry(
            new PackageKey("Contoso.App", "source-a"),
            PackageOperationKind.Install,
            PackageOperationState.Installing);

        var row = Apply(
            search,
            [search],
            queue: new OperationQueueSnapshot { Current = otherSourceOperation });

        Assert.Null(row.OperationState);
        Assert.Equal("Install", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
    }

    [Fact]
    public void ExactAdministratorRequiredUpgrade_RemainsDisabledAfterItLeavesTheQueue()
    {
        var search = Package("Contoso.App", "source-a");
        var update = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.UpdateAvailable,
            installed: "1.0",
            available: "2.0");
        var failed = AdministratorRequiredResult(search.Key, PackageOperationKind.Upgrade);

        var row = Apply(
            search,
            [search],
            updates: [update],
            queue: new OperationQueueSnapshot { History = [failed] });

        Assert.Equal(PackageOperationState.Failed, row.OperationState);
        Assert.Equal(WingetErrorKind.AdministratorRequired, row.OperationErrorKind);
        Assert.Equal("Administrator required - see Activity for details", row.Status);
        Assert.Equal("Admin required", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
        Assert.True(row.RequiresAdministratorRetry);
    }

    [Fact]
    public void ExactAdministratorRequiredUpgrade_OffersOnlyExplicitAdministratorRetryWhenAvailable()
    {
        var search = Package("Contoso.App", "source-a");
        var update = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.UpdateAvailable,
            installed: "1.0",
            available: "2.0");
        var failed = AdministratorRequiredResult(search.Key, PackageOperationKind.Upgrade);
        var queue = new OperationQueueSnapshot { History = [failed] };
        var index = DiscoverRowProjector.CreateIndex([search], [], [update], queue);

        var row = DiscoverRowProjector.Apply(
            Row(search),
            search,
            index,
            administratorRetryAvailable: true);

        Assert.Equal("Retry as administrator", row.ActionLabel);
        Assert.Equal("Administrator approval required - elevated retry available", row.Status);
        Assert.True(row.IsActionEnabled);
        Assert.True(row.RequiresAdministratorRetry);
        Assert.Equal(PackageOperationKind.Upgrade, row.RequestedOperationKind);
    }

    [Fact]
    public void CancelledAdministratorApproval_PreservesExplicitAdministratorRetry()
    {
        var search = Package("Contoso.App", "source-a");
        var update = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.UpdateAvailable,
            installed: "1.0",
            available: "2.0");
        var failed = AdministratorRequiredResult(search.Key, PackageOperationKind.Upgrade) with
        {
            AdministratorRetryRequested = true,
            Error = new WingetError
            {
                Kind = WingetErrorKind.ElevationDenied,
                Code = "ElevationCancelled",
                Message = "Administrator approval was canceled."
            }
        };
        var queue = new OperationQueueSnapshot { History = [failed] };
        var index = DiscoverRowProjector.CreateIndex([search], [], [update], queue);

        var row = DiscoverRowProjector.Apply(
            Row(search),
            search,
            index,
            administratorRetryAvailable: true);

        Assert.Equal("Retry as administrator", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
        Assert.True(row.RequiresAdministratorRetry);
        Assert.Equal(PackageOperationKind.Upgrade, row.RequestedOperationKind);
    }

    [Fact]
    public void AdministratorRequiredHistoryForAnotherSource_DoesNotAffectRow()
    {
        var search = Package("Contoso.App", "source-b");
        var update = Package(
            "Contoso.App",
            "source-b",
            status: PackageStatus.UpdateAvailable,
            installed: "1.0",
            available: "2.0");
        var otherSourceFailure = AdministratorRequiredResult(
            new PackageKey("Contoso.App", "source-a"),
            PackageOperationKind.Upgrade);

        var row = Apply(
            search,
            [search],
            updates: [update],
            queue: new OperationQueueSnapshot { History = [otherSourceFailure] });

        Assert.Null(row.OperationState);
        Assert.Null(row.OperationErrorKind);
        Assert.Equal("Update", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
    }

    [Fact]
    public void NewerTerminalResultSupersedesAdministratorRequiredHistory()
    {
        var search = Package("Contoso.App", "source-a");
        var update = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.UpdateAvailable,
            installed: "1.0",
            available: "2.0");
        var failed = AdministratorRequiredResult(search.Key, PackageOperationKind.Upgrade);
        var newer = failed with
        {
            OperationId = Guid.NewGuid(),
            State = PackageOperationState.Cancelled,
            Error = null,
            CompletedAt = failed.CompletedAt.AddMinutes(1)
        };

        var row = Apply(
            search,
            [search],
            updates: [update],
            queue: new OperationQueueSnapshot { History = [failed, newer] });

        Assert.Null(row.OperationState);
        Assert.Equal("Update", row.ActionLabel);
        Assert.True(row.IsActionEnabled);
    }

    [Fact]
    public void MutationVerification_DisablesOtherwiseActionableResult()
    {
        var search = Package("Contoso.App", "source-a");
        var index = DiscoverRowProjector.CreateIndex(
            [search],
            [],
            [],
            new OperationQueueSnapshot());

        var row = DiscoverRowProjector.Apply(
            Row(search),
            search,
            index,
            mutationVerificationPending: true,
            mutationVerificationPhase: PackagePilot.Core.Services.MutationVerificationPhase.VerificationPending);

        Assert.Equal("Verifying", row.ActionLabel);
        Assert.False(row.IsActionEnabled);
        Assert.Equal(PackageOperationState.Completed, row.OperationState);
    }

    [Fact]
    public void ApplicationRestartPending_ShowsHonestStagedUpdateState()
    {
        var search = Package(
            "Contoso.App",
            "source-a",
            status: PackageStatus.UpdateAvailable,
            installed: "1.0",
            available: "2.0");
        var index = DiscoverRowProjector.CreateIndex(
            [search],
            [search],
            [search],
            new OperationQueueSnapshot());

        var row = DiscoverRowProjector.Apply(
            Row(search),
            search,
            index,
            mutationVerificationPending: true,
            mutationVerificationPhase:
                PackagePilot.Core.Services.MutationVerificationPhase.ApplicationRestartPending);

        Assert.Equal("App restart needed", row.ActionLabel);
        Assert.Contains("close and reopen", row.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(row.IsActionEnabled);
        Assert.Equal(PackageOperationState.Completed, row.OperationState);
    }

    private static PackageListItem Apply(
        PackageSummary search,
        IReadOnlyList<PackageSummary> searchResults,
        IReadOnlyList<PackageSummary>? installed = null,
        IReadOnlyList<PackageSummary>? updates = null,
        OperationQueueSnapshot? queue = null)
    {
        var index = DiscoverRowProjector.CreateIndex(
            searchResults,
            installed ?? [],
            updates ?? [],
            queue ?? new OperationQueueSnapshot());
        return DiscoverRowProjector.Apply(Row(search), search, index);
    }

    private static PackageListItem Row(PackageSummary package) => new()
    {
        Name = package.Name,
        PackageId = package.Key.Id,
        Source = package.Key.SourceId,
        InstalledVersion = package.InstalledVersion ?? string.Empty,
        AvailableVersion = package.AvailableVersion ?? string.Empty,
        WingetPackage = package.Key
    };

    private static PackageSummary Package(
        string id,
        string source,
        string? name = null,
        PackageStatus status = PackageStatus.Available,
        string? installed = null,
        string? available = "1.0") => new()
        {
            Key = new PackageKey(id, source),
            Name = name ?? id,
            Publisher = "Contoso",
            Status = status,
            InstalledVersion = installed,
            AvailableVersion = available,
            SourceName = source
        };

    private static OperationQueueEntry Entry(
        PackageKey package,
        PackageOperationKind kind,
        PackageOperationState state) => new(
            PackageOperation.Create(kind, package),
            new OperationProgress
            {
                OperationId = Guid.NewGuid(),
                State = state
            });

    private static OperationResult AdministratorRequiredResult(
        PackageKey package,
        PackageOperationKind kind)
    {
        var operationId = Guid.NewGuid();
        return new OperationResult
        {
            OperationId = operationId,
            Package = package,
            Target = new WingetTarget { Package = package },
            Kind = kind,
            State = PackageOperationState.Failed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Error = new WingetError
            {
                Kind = WingetErrorKind.AdministratorRequired,
                Code = "InstallError:-2147001048",
                Message = "Windows requires administrator privileges.",
                HResult = unchecked((int)0x80073D28)
            }
        };
    }
}
