# Package Pilot

Package Pilot is a native Windows 11 application hub. It combines WinGet, current-user MSIX/Store packages, and visible legacy application records in a Fluent, normal-integrity interface while delegating supported package work to Windows APIs.

## Current behavior

- Discover, Installed, Updates, Activity, Sources, and Settings destinations
- Cached, page-local update state that never blocks startup; daily read-only background discovery by default, with six-hour and Manual options
- One deduplicated update notification, taskbar/Start badge, Jump List tasks, protocol activation, execution alias, and single-instance redirection
- Optional native notification-area mode, off by default; Close hides only when enabled, Minimize stays conventional, and the dependent Start with Windows option launches a lightweight hidden resident host
- Unified installed-app inventory from WinGet, MSIX/Store, and HKCU/HKLM 32/64-bit uninstall metadata, merged only by exact package identities
- Safe routing to WinGet, current-user MSIX removal, Microsoft Store updates, or Windows Installed Apps according to provider capability
- WinGet contract 6+ capability gate with friendly App Installer, policy, network, authentication, installer, elevation, cancellation, and reboot states
- Exact per-source and package agreement consent; changed terms invalidate prior source consent and are never accepted silently
- Supported source refresh/add/remove/reset-one/explicit editing through WinGet COM and an allowlisted one-shot UAC helper
- Sequential install/update/uninstall queue with a safe cancellation boundary
- Confirmed uninstalls and staged multi-package update review
- 100-result search cap, stale-search cancellation, partial-source health, and read-only source diagnostics
- Mica, system accent, light/dark/high-contrast resources, responsive details, keyboard shortcuts, and Narrator labels/live status
- Preferences in `ApplicationData.LocalSettings`, the latest 100 operation results in local JSON, and validated WinGet icons in `ApplicationData.LocalCacheFolder`
- No Package Pilot telemetry

## Requirements

- Windows 11 x64 or ARM64, build 22000 or later
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

Then open `PackagePilot.appinstaller`. Installing through this file selects the native x64 or ARM64 package from `PackagePilot.msixbundle`, installs the matching Windows App Runtime dependency, and registers the public release feed with Windows App Installer. Installing the bundle directly works only when its dependencies and certificate are already present, but does not register automatic update settings.

## Releases and automatic updates

Every successful push to `main` runs deterministic tests, assigns a monotonically increasing four-part MSIX version, and retains an unsigned release payload as a GitHub Actions artifact for 30 days. The release sequence is encoded across the MSIX build and revision fields so no individual field exceeds Windows' 65,535 limit.

The signing key never leaves the release maintainer's Windows certificate store. Initialize it once from an elevated PowerShell window:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\Initialize-ManualReleaseCertificate.ps1
```

After a successful `Release` workflow run, review the trusted checkout and select that exact run ID. Preparation requires the run to be the newest successful `Release` run and its commit to still be the exact current `main` commit. It downloads and validates the unsigned artifact, signs it locally, and atomically moves the result into a new durable directory without creating a tag or GitHub release. The publisher requires an authenticated GitHub CLI session (`gh auth login`):

```powershell
$runId = <workflow-run-id>
$runNumber = [uint64](gh run view $runId `
  --repo nativepapaya/package-pilot `
  --json number `
  --jq .number)
$tag = "v1.0.$($runNumber + 4)"
$certificateThumbprint = '<initializer-output-thumbprint>'
$preparedDirectory = Join-Path `
  $env:USERPROFILE `
  "Documents\PackagePilot-Releases\$tag"

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\Publish-ManualRelease.ps1 `
  -Prepare `
  -RunId $runId `
  -PreparedDirectory $preparedDirectory `
  -CertificateThumbprint $certificateThumbprint
```

Preparation fails rather than overwriting an existing directory. The directory contains the exact signed assets, the hosted `release-metadata.json`, and a detached, certificate-signed `prepared-release.json` that binds the run, commit, version, signer, file sizes, and SHA-256 hashes. Keep the directory unchanged while completing the packaged startup, tray, background-wake, performance, and staged N-to-N+1 servicing gates.

When every pre-publication gate passes, promote only that prepared directory. `-RunId`, the exact derived `-ConfirmTag`, and the tested bundle hash are mandatory safety tokens:

```powershell
$bundleSha256 = (Get-FileHash `
  -LiteralPath (Join-Path $preparedDirectory 'PackagePilot.msixbundle') `
  -Algorithm SHA256).Hash.ToLowerInvariant()

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\Publish-ManualRelease.ps1 `
  -Promote `
  -RunId $runId `
  -ConfirmTag $tag `
  -ConfirmBundleSha256 $bundleSha256 `
  -PreparedDirectory $preparedDirectory `
  -CertificateThumbprint $certificateThumbprint
```

Promotion revalidates the signed state, local assets, certificate, current `main`, workflow attempt, release high-water mark, and App Installer policy before any GitHub mutation. It creates a non-latest draft, uploads only missing assets without replacing an existing asset, verifies every GitHub digest, and then publishes it as the latest stable release. Rerunning promotion safely resumes an exact matching draft or verifies an already-published exact release; a conflicting tag, asset, signer, run attempt, newer successful run, or modified prepared file fails closed.

Failed or cancelled workflow runs may leave sequence gaps because the release sequence is the workflow run number plus four. Never create the version tag manually and never prepare an older successful run while a newer successful run exists. A newer `main` commit makes an already-prepared candidate intentionally stale and requires preparing its new workflow run.

The hosted workflow produces separate unsigned x64 and ARM64 packages, validates their identities and required read-only/background payloads, and combines them with the Windows SDK's `MakeAppx` tool. Stable asset names let the App Installer feed follow the latest release without embedding credentials or GitHub API tokens in the app.

> [!IMPORTANT]
> GitHub draft assets are not an anonymous update channel, while the production `PackagePilot.appinstaller` deliberately points to `releases/latest/download`. The exact signed bundle can be tested before promotion, but an end-to-end test through the production GitHub/App Installer URL necessarily happens only after that release becomes public/latest. Use a disposable local HTTPS staging feed for pre-publication App Installer mechanics, then perform the production-feed check immediately after promotion and issue a newer release rather than altering a published asset if it fails.

Copies installed through `PackagePilot.appinstaller` perform a quiet launch check no more than once every 24 hours, never block activation, and also register App Installer's background check. Settings shows the installed version and can query the same App Installer association. If a copy was installed directly from the bundle, use **Get update installer** once to connect it to the feed.

The certificate initializer sets `KeyExportPolicy` to `NonExportable`; neither a PFX nor a signing password is created or stored in GitHub. Losing the Windows profile or machine can permanently lose this release key unless a verified system-level backup preserves non-exportable keys.

The repository still uses the development identity `PackagePilot.Desktop` / `CN=PackagePilot.Dev`. Production publication is intentionally blocked until a permanent certificate-backed publisher is selected. Freeze the final package `Name` and certificate-matching `Publisher` together before distributing to users; changing the publisher later is a new package identity and requires installing the new trusted package rather than an in-place MSIX update. A neutral migration service is retained and tested as deferred infrastructure, but the current GitHub package does not run an identity export or import hook. Before distributing Package Pilot beyond trusted testers, replace development signing with a publicly trusted signing service.

## Build and test

Install the .NET SDK version pinned by `global.json`, then use `dotnet` from your normal development shell.

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

dotnet restore PackagePilot.slnx
dotnet build PackagePilot.slnx -c Release
dotnet build src\PackagePilot.App\PackagePilot.App.csproj -c Release -r win-arm64 -p:Platform=ARM64
dotnet test tests\PackagePilot.Tests\PackagePilot.Tests.csproj -c Release
```

The eight live integration tests are read-only and opt-in. They activate WinGet COM, exercise the concurrent startup read model, enumerate sources, search, read installed inventory and metadata, detect updates, and compare combined-catalog results with the per-source reference path; they never install or remove packages.

```powershell
$env:PACKAGEPILOT_RUN_LIVE_WINGET_TESTS = '1'
dotnet test tests\PackagePilot.Tests\PackagePilot.Tests.csproj -c Release --filter 'FullyQualifiedName~PackagePilot.Tests.Integration'
```

## Create the MSIX

Single-project MSIX creates one package at a time, so build unsigned framework-dependent packages for both architectures:

```powershell
$packageRoots = @{}
foreach ($build in @(
  @{ Platform = 'x64'; Runtime = 'win-x64' }
  @{ Platform = 'ARM64'; Runtime = 'win-arm64' }
)) {
  $packageRoots[$build.Platform] = Join-Path $PWD "artifacts\$($build.Platform)"
  dotnet publish src\PackagePilot.App\PackagePilot.App.csproj `
    -c Release -r $build.Runtime --self-contained false `
    -p:Platform=$($build.Platform) `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=false `
    -p:AppxBundle=Never `
    -p:AppxPackageDir="$($packageRoots[$build.Platform])\"
}
```

Then combine the two `PackagePilot.Desktop` MSIX files. The bundler validates that they are unsigned, have identical names, publishers, and versions, and contain exactly x64 and ARM64 architectures:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\New-MsixBundle.ps1 `
  -X64PackagePath (Get-ChildItem $packageRoots.x64 -Recurse -Filter '*.msix' | Where-Object Name -Like 'PackagePilot*').FullName `
  -Arm64PackagePath (Get-ChildItem $packageRoots.ARM64 -Recurse -Filter '*.msix' | Where-Object Name -Like 'PackagePilot*').FullName `
  -OutputPath .\artifacts\PackagePilot.msixbundle
```

The release workflow performs the same build and bundle validation automatically, but leaves the bundle unsigned for the offline manual publisher.

For a local install, `build\New-DevelopmentCertificate.ps1` creates a non-exportable code-signing key in `Cert:\CurrentUser\My` and, from an elevated process, trusts its public certificate in `Cert:\LocalMachine\TrustedPeople`. App Installer requires the computer store for a self-signed MSIX bundle. This is a machine-wide, persistent trust change intended only for local testing and should be run only after reviewing and explicitly approving it. Sign the completed bundle with the returned certificate thumbprint, and remove the development certificate when local testing is finished.

## Keyboard

- `Ctrl+F`: open Discover and focus search
- `Ctrl+R`: refresh only the visible destination
- `Ctrl+1` through `Ctrl+6`: Discover, Installed, Updates, Activity, Sources, Settings

## Safety notes

Package Pilot starts and remains at normal integrity. WinGet or an installer can show UAC when a reviewed package requires it. If WinGet instead reports that a packaged-service install requires an elevated host, only that exact failed row offers **Retry as administrator**. Package Pilot reviews current agreements again, shows the exact package, source, and action, and then launches a short-lived `PackagePilot.PackageAdmin` helper through one Windows UAC prompt. The helper accepts one authenticated, allowlisted WinGet request, returns one result, and exits; it is never used automatically or by bulk updates. The helper accepts approval only for the initiating Windows account; alternate administrator credentials are rejected so a different user's package profile cannot be changed. If the result channel is lost after dispatch, Package Pilot blocks another mutation until its normal recovery check verifies the package state. Supported source mutations use a separate `PackagePilot.SourceAdmin` helper and protocol. Queued operations can be cancelled, but an administrator retry becomes non-cancelable when UAC preparation starts. Registry uninstall commands are never read or executed.
