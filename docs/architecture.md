# Package Pilot architecture

## Projects

- `PackagePilot.App` owns the WinUI shell, review dialogs, activation handoff, and identity-scoped application state.
- `PackagePilot.Core` contains provider-neutral records, interfaces, exact inventory merging, update coordination, queueing, migration, and atomic JSON stores.
- `PackagePilot.Windows.ReadOnly` contains update discovery, badge integration, and identity-bound constants shared by the foreground app and isolated background host.
- `PackagePilot.Windows` is the mutation-capable Windows infrastructure layer for WinGet COM, unified inventory, source administration, startup registration, notifications, and notification-area integration.
- `PackagePilot.Background` is the packaged, read-only update-discovery COM host.
- `PackagePilot.SourceAdmin` is the short-lived elevated source-administration helper.
- `PackagePilot.Tests` contains deterministic tests plus opt-in read-only WinGet integration tests.

## Trust boundaries

`IWingetClient` is the only WinGet dependency visible to Core. `WingetClient` converts `Microsoft.Management.Deployment` COM/WinRT objects into stable domain records and translates HRESULT/status combinations into Package Pilot errors. The ComInterop static factory shim activates WinGet's out-of-process COM server; Package Pilot does not embed the in-process WinGet engine or construct CLI commands.

All package mutations pass through `IOperationQueue` and require foreground review. The queue accepts provider-neutral `WingetTarget` and `MsixTarget` values. MSIX removal stops honoring cancellation before `RemovePackageAsync` begins. Registry uninstall command values are never read or executed.

Source refresh runs normally. Add, remove, reset-one, and explicit-source edits cross a random, one-shot, authenticated named pipe to `PackagePilot.SourceAdmin`, which validates an allowlisted request, performs one WinGet COM operation after UAC approval, returns one result, and exits. There is no reset-all or arbitrary-command representation.

The background host can only discover updates. Foreground and background checks share a named mutex and atomically replace the same snapshot. Background failures are recorded for a deferred foreground retry. There is no separate tray executable or Task Scheduler fallback; when notification-area mode is enabled, the main app can remain as a lightweight resident host and creates the mutation-capable foreground graph only when a window is opened.

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

The neutral identity-migration service and its deterministic tests are retained as deferred infrastructure for a future production identity. The current GitHub package does not run an export or import hook.

No Package Pilot telemetry pipeline is present.
