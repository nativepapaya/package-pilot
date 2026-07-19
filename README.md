# Package Pilot

Package Pilot is a native Windows 11 package manager for discovering, installing, updating, and removing applications. It combines WinGet, MSIX/Store packages, and visible legacy applications in one Fluent interface.

Package changes are always reviewed in the foreground. Background work is read-only.

## Highlights

- Discover and manage applications through WinGet
- Unified installed-app inventory across WinGet, MSIX/Store, and the registry
- Quiet update monitoring with cached results, badges, and deduplicated notifications
- Live operation progress, history, diagnostics, and WinGet logs
- Safe source management through WinGet COM
- Optional notification-area mode and Start with Windows
- Native x64 and ARM64 packages with light, dark, and high-contrast support
- No telemetry

## Install

Download `PackagePilot.cer`, `PackagePilot.appinstaller`, and `SHA256SUMS.txt` from the [latest release](https://github.com/nativepapaya/package-pilot/releases/latest). Verify the published hashes before installing.

> [!WARNING]
> Current releases use a self-signed development certificate and are intended for development and personal testing. Trust it only after reviewing this repository and the downloaded certificate.

From an elevated PowerShell window, trust the certificate once:

```powershell
Import-Certificate `
  -FilePath .\PackagePilot.cer `
  -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Open `PackagePilot.appinstaller` to install the correct x64 or ARM64 package and register automatic updates. Installing the bundle directly does not register the update feed.

## Requirements

- Windows 11 x64 or ARM64, build 22000 or later
- A current App Installer/WinGet release with deployment API contract 6 or later
- .NET 10 SDK and Developer Mode for local development

## Build and test

Use the SDK pinned by `global.json`:

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

dotnet restore PackagePilot.slnx
dotnet build PackagePilot.slnx -c Release
dotnet test tests\PackagePilot.Tests\PackagePilot.Tests.csproj -c Release
```

Build the ARM64 app explicitly:

```powershell
dotnet build src\PackagePilot.App\PackagePilot.App.csproj `
  -c Release -r win-arm64 -p:Platform=ARM64
```

The optional live WinGet tests are read-only:

```powershell
$env:PACKAGEPILOT_RUN_LIVE_WINGET_TESTS = '1'
dotnet test tests\PackagePilot.Tests\PackagePilot.Tests.csproj `
  -c Release --filter 'FullyQualifiedName~PackagePilot.Tests.Integration'
```

## Safety

Package Pilot normally runs without elevation. When a reviewed operation requires administrator access, it uses a short-lived, authenticated helper for that exact request. It never executes registry uninstall commands, silently accepts agreements, or performs package/source mutations in the background. Operations that cannot be managed safely are handed off to Windows Settings or Microsoft Store.

## Keyboard shortcuts

- `Ctrl+F` — open Discover and focus search
- `Ctrl+R` — refresh the current destination
- `Ctrl+1` through `Ctrl+6` — switch destinations

## Documentation

- [Architecture](docs/architecture.md)
- [Release and packaging guide](docs/releasing.md)
