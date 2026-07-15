# Package Pilot

Package Pilot is a native Windows 11 package center for WinGet. It provides a Fluent, normal-integrity interface for discovering, installing, updating, and removing applications while delegating package work to the official out-of-process `Microsoft.Management.Deployment` API.

## V1 behavior

- Discover, Installed, Updates, Activity, and Settings destinations
- WinGet contract 6+ capability gate with friendly App Installer, policy, network, authentication, installer, elevation, cancellation, and reboot states
- Explicit source and package agreement consent; terms are never accepted silently
- Sequential install/update/uninstall queue with a safe cancellation boundary
- Confirmed uninstalls and staged multi-package update review
- 100-result search cap, stale-search cancellation, partial-source health, and read-only source diagnostics
- Mica, system accent, light/dark/high-contrast resources, responsive details, keyboard shortcuts, and Narrator labels/live status
- Preferences in `ApplicationData.LocalSettings`, the latest 100 operation results in local JSON, and validated WinGet icons in `ApplicationData.LocalCacheFolder`
- No Package Pilot telemetry

## Requirements

- Windows 11 x64, build 22000 or later
- .NET 10 SDK for development and the .NET 10 Desktop runtime for a framework-dependent install
- Developer Mode for the supported `dotnet run` workflow
- A current App Installer / WinGet with deployment API contract 6 or later

The repository pins Windows App SDK 2.2.0, CommunityToolkit.Mvvm 8.4.2, and Microsoft.WindowsPackageManager.ComInterop 1.28.240.

## Install a development release

Download `PackagePilot.cer`, `PackagePilot.appinstaller`, and `SHA256SUMS.txt` from the [latest GitHub release](https://github.com/nativepapaya/package-pilot/releases/latest). Compare the certificate and App Installer file hashes with their entries in `SHA256SUMS.txt` before opening them. Matching checksums detect an incomplete or corrupted download; because the checksum file is published with the release, it is not an independent guarantee if the GitHub repository or release itself is compromised.

> [!WARNING]
> Package Pilot releases currently use a self-signed development certificate. Trusting that certificate allows packages signed by its private key to install and update on the computer. Review the public repository, release checksums, and certificate before proceeding. This is suitable for development and personal testing, not broad production distribution.

From an elevated PowerShell window, trust the reviewed public certificate once:

```powershell
Import-Certificate `
  -FilePath .\PackagePilot.cer `
  -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Then open `PackagePilot.appinstaller`. Installing through this file registers the public release feed with Windows App Installer. Installing `PackagePilot.msix` directly works only when its dependencies and certificate are already present, but does not register automatic update settings.

## Releases and automatic updates

Every successful push to `main` runs deterministic tests, assigns a monotonically increasing four-part MSIX version, and retains an unsigned release payload as a GitHub Actions artifact for 30 days. The release sequence is encoded across the MSIX build and revision fields so no individual field exceeds Windows' 65,535 limit.

The signing key never leaves the release maintainer's Windows certificate store. Initialize it once from an elevated PowerShell window:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\Initialize-ManualReleaseCertificate.ps1
```

After a successful `Release` workflow run, review the trusted checkout and publish the oldest unpublished payload from a normal PowerShell window. The publisher requires an authenticated GitHub CLI session (`gh auth login`):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\Publish-ManualRelease.ps1
```

Failed or cancelled workflow runs may leave sequence gaps; the publisher safely chooses the oldest successful run newer than the current release. To deliberately skip an obsolete successful payload, pass its replacement as `-RunId <workflow-run-id>`. Publishing the newer run permanently prevents older versions from being released afterward.

The publisher downloads and validates the GitHub artifact, signs `PackagePilot.msix` with the non-exportable local key, verifies both package signatures and identities, creates checksums, and publishes the matching `v1.0.<release-sequence>` release. It refuses to replace an existing release. Stable asset names let the App Installer feed follow the latest release without embedding credentials or GitHub API tokens in the app.

Copies installed through `PackagePilot.appinstaller` check for updates at launch without blocking activation and also register a background check. Settings shows the installed version and can query the same App Installer association. If a copy was installed directly from an MSIX, use **Get update installer** once to connect it to the feed.

The certificate initializer sets `KeyExportPolicy` to `NonExportable`; neither a PFX nor a signing password is created or stored in GitHub. Losing the Windows profile or machine can permanently lose this release key unless a verified system-level backup preserves non-exportable keys. Before distributing Package Pilot beyond trusted testers, replace development signing with a publicly trusted signing service or Microsoft Store signing.

## Build and test

This workspace has a project-local SDK in `.dotnet`. If .NET 10 is installed system-wide, replace `.\.dotnet\dotnet.exe` with `dotnet`.

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

.\.dotnet\dotnet.exe restore PackagePilot.slnx
.\.dotnet\dotnet.exe build PackagePilot.slnx -c Release
.\.dotnet\dotnet.exe test tests\PackagePilot.Tests\PackagePilot.Tests.csproj -c Release
```

The six live integration tests are read-only and opt-in. They activate WinGet COM, enumerate sources, search, read installed inventory and metadata, and detect updates; they never install or remove packages.

```powershell
$env:PACKAGEPILOT_RUN_LIVE_WINGET_TESTS = '1'
.\.dotnet\dotnet.exe test tests\PackagePilot.Tests\PackagePilot.Tests.csproj -c Release --filter 'FullyQualifiedName~PackagePilot.Tests.Integration'
```

## Create the MSIX

Create an unsigned framework-dependent x64 package:

```powershell
.\.dotnet\dotnet.exe publish src\PackagePilot.App\PackagePilot.App.csproj `
  -c Release -p:Platform=x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=false `
  -p:AppxBundle=Never
```

Output is written under `src\PackagePilot.App\AppPackages`.

For a local install, `build\New-DevelopmentCertificate.ps1` creates a non-exportable code-signing key in `Cert:\CurrentUser\My` and, from an elevated process, trusts its public certificate in `Cert:\LocalMachine\TrustedPeople`. App Installer requires the computer store for a self-signed MSIX. This is a machine-wide, persistent trust change intended only for local testing and should be run only after reviewing and explicitly approving it. Use the returned thumbprint as `PackageCertificateThumbprint` with `AppxPackageSigningEnabled=true`, and remove the development certificate when local testing is finished.

## Keyboard

- `Ctrl+F`: open Discover and focus search
- `Ctrl+R`: refresh package state
- `Ctrl+1` through `Ctrl+5`: Discover, Installed, Updates, Activity, Settings

## Safety notes

Package Pilot starts at normal integrity and does not request administrator rights itself. WinGet or an installer can show UAC only when a selected package requires it. Queued, resolving, and downloading operations can be cancelled; after an installer takes control, cancellation is no longer guaranteed.
