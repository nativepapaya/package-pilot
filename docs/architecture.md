# Package Pilot architecture

## Projects

- `PackagePilot.App` owns WinUI 3, packaged application state, the WinGet COM adapter, and presentation logic.
- `PackagePilot.Core` contains framework-neutral records, interfaces, queueing, error contracts, and JSON history persistence.
- `PackagePilot.Tests` contains deterministic Core tests and opt-in read-only WinGet integration tests.

## Boundaries

`IWingetClient` is the only package-management dependency visible to the Core layer. `WingetClient` converts `Microsoft.Management.Deployment` COM/WinRT objects into stable domain records and translates HRESULT/status combinations into `WingetError` values. The ComInterop static activation-factory shim targets WinGet's out-of-process COM server; Package Pilot does not embed the in-process WinGet engine.

`IOperationQueue` serializes all mutations. Each operation moves through queued, resolving, downloading, installer-controlled execution, and a terminal result. Cancellation tokens reach WinGet only before installer-controlled execution.

## Data flow

1. The shell checks WinGet availability and API contract 6.
2. Read-only queries populate package/source domain records.
3. The UI presents source/package agreements and elevation expectations.
4. Explicitly approved operations enter the sequential queue.
5. Progress is announced and retained; successful mutations trigger asynchronous Installed and Updates refreshes.

## Local data

- `ApplicationData.LocalSettings`: theme, motion, scope, and architecture preferences
- `ApplicationData.LocalFolder/operation-history.json`: up to 100 terminal results
- `ApplicationData.LocalCacheFolder/PackagePilot/Icons`: HTTPS-only, size-bounded, decoder-validated, canonical PNG package icons

No Package Pilot telemetry pipeline is present.
