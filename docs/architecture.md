# Package Pilot architecture

## Projects

- `PackagePilot.App` owns the WinUI shell, review dialogs, activation handoff, and identity-scoped application state.
- `PackagePilot.Core` contains provider-neutral records, interfaces, exact inventory merging, update coordination, queueing, migration, and atomic JSON stores.
- `PackagePilot.Windows` is the reusable Windows infrastructure layer for WinGet COM, notification/badge integration, and identity-bound constants.
- `PackagePilot.Background` is the packaged, read-only update-discovery COM host.
- `PackagePilot.SourceAdmin` is the short-lived elevated source-administration helper.
- `PackagePilot.Tests` contains deterministic tests plus opt-in read-only WinGet integration tests.

## Trust boundaries

`IWingetClient` is the only WinGet dependency visible to Core. `WingetClient` converts `Microsoft.Management.Deployment` COM/WinRT objects into stable domain records and translates HRESULT/status combinations into Package Pilot errors. The ComInterop static factory shim activates WinGet's out-of-process COM server; Package Pilot does not embed the in-process WinGet engine or construct CLI commands.

All package mutations pass through `IOperationQueue` and require foreground review. The queue accepts provider-neutral `WingetTarget` and `MsixTarget` values. MSIX removal stops honoring cancellation before `RemovePackageAsync` begins. Registry uninstall command values are never read or executed.

Source refresh runs normally. Add, remove, reset-one, and explicit-source edits cross a random, one-shot, authenticated named pipe to `PackagePilot.SourceAdmin`, which validates an allowlisted request, performs one WinGet COM operation after UAC approval, returns one result, and exits. There is no reset-all or arbitrary-command representation.

The background host can only discover updates. Foreground and background checks share a named mutex and atomically replace the same snapshot. Background failures are recorded for a deferred foreground retry; no tray process or Task Scheduler fallback exists.

## Inventory and update flow

1. The shell renders the last successful update snapshot before any stale scan begins.
2. WinGet, current-user MSIX/Store, and HKCU/HKLM Registry32/Registry64 providers run independently.
3. The merger joins only exact package-family names or canonical product codes; display names, publishers, and versions are never join keys.
4. Read-only update checks publish normalized fingerprints to the snapshot, badge, and deduplicated notification policy.
5. Explicitly approved operations run sequentially; successful mutations refresh only Installed and Updates.

## Local data

- `ApplicationData.LocalSettings`: theme, motion, cadence, install preferences, and exact per-source agreement fingerprints
- `ApplicationData.LocalFolder/operation-history.json`: up to 100 terminal results
- `ApplicationData.LocalFolder/update-snapshot.json`: versioned update rows, timestamps, fingerprints, and source health
- `ApplicationData.LocalFolder/background-update-status.json`: last opportunistic background-host outcome
- `ApplicationData.LocalCacheFolder/PackagePilot/Icons`: HTTPS-only, size-bounded, decoder-validated package icons
- `%LOCALAPPDATA%/Package Pilot/Identity Migration`: atomic one-time settings/history handoff between the retiring development identity and the permanent production identity

No Package Pilot telemetry pipeline is present.
