using PackagePilot.Core.Services;
using PackagePilot.Windows.Services;
using Windows.Storage;

namespace PackagePilot.Background;

internal static class BackgroundHostFactory
{
    internal static BackgroundUpdateRunner CreateRunner()
    {
        var localFolder = ApplicationData.Current.LocalFolder.Path;
        var settings = ApplicationData.Current.LocalSettings.Values;
        var storedCadence = settings.TryGetValue("updateMonitoringCadence", out var stored)
            ? stored as string
            : null;
        var cadence = UpdateMonitoringPolicy.ParseCadence(storedCadence);
        var snapshotStore = new JsonUpdateSnapshotStore(Path.Combine(
            localFolder,
            "update-snapshot.json"));
        var statusStore = new JsonBackgroundUpdateRunStatusStore(Path.Combine(
            localFolder,
            "background-update-status.json"));
        var coordinator = new UpdateCoordinator(new WindowsUpdateDiscoveryClient(), snapshotStore);
        var worker = new UpdateScanWorker(
            coordinator,
            new UpdateNotificationPolicy(),
            new WindowsNotificationSink());
        return new BackgroundUpdateRunner(worker, statusStore, cadence: cadence);
    }
}
