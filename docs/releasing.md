# Release and packaging guide

Package Pilot uses GitHub Actions for validation and unsigned packaging, followed by local signing and publication. The signing key never leaves the maintainer's Windows certificate store.

Run maintainer commands with PowerShell 7.5 or later (`pwsh`). Release JSON is checked against committed schemas, native-tool failures include bounded structured diagnostics, and isolated x64/ARM64 package builds run concurrently. Signing and GitHub publication remain sequential. Safety-critical scripts remain compatible with Windows PowerShell 5.1, which is retained as a separate CI security gate. Package Pilot itself does not use PowerShell at runtime.

GitHub workflows intentionally use the PowerShell bundled with the pinned `windows-2025` runner image and fail before build or packaging if it is older than 7.5. Runner servicing supplies PowerShell security updates; the explicit version gate prevents a silent fallback to an older host.

## Release model

Each successful push to `main` runs deterministic tests, assigns a monotonically increasing four-part MSIX version, and retains an unsigned release payload for 30 days. The workflow produces x64 and ARM64 packages and combines them into one bundle.

Public releases use stable asset names so `PackagePilot.appinstaller` can follow the latest release. App Installer checks quietly at most once every 24 hours and also registers its background update task.

The repository currently uses the development identity `PackagePilot.Desktop` / `CN=PackagePilot.Dev`. Do not distribute it broadly until a permanent, publicly trusted publisher is selected. Changing the publisher later creates a new package identity and cannot be delivered as an in-place update.

## Initialize the signing certificate

From an elevated PowerShell window:

```powershell
pwsh -NoProfile -File .\build\Initialize-ManualReleaseCertificate.ps1
```

The certificate is non-exportable. Losing the Windows profile or machine may permanently lose the key unless a verified system-level backup preserves it.

## Prepare a release

Use an authenticated GitHub CLI session (`gh auth login`). Select the newest successful `Release` workflow run whose commit is still the current `main` commit:

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

pwsh -NoProfile -File .\build\Publish-ManualRelease.ps1 `
  -Prepare `
  -RunId $runId `
  -PreparedDirectory $preparedDirectory `
  -CertificateThumbprint $certificateThumbprint
```

Preparation downloads and validates the unsigned artifact, signs it locally, and creates an immutable candidate directory. It fails instead of overwriting an existing directory. Complete the packaged startup, tray, background wake, performance, and staged N-to-N+1 update checks against this exact candidate.

## Promote a tested release

After every release gate passes:

```powershell
$bundleSha256 = (Get-FileHash `
  -LiteralPath (Join-Path $preparedDirectory 'PackagePilot.msixbundle') `
  -Algorithm SHA256).Hash.ToLowerInvariant()

pwsh -NoProfile -File .\build\Publish-ManualRelease.ps1 `
  -Promote `
  -RunId $runId `
  -ConfirmTag $tag `
  -ConfirmBundleSha256 $bundleSha256 `
  -PreparedDirectory $preparedDirectory `
  -CertificateThumbprint $certificateThumbprint
```

Promotion revalidates the candidate, signer, commit, workflow attempt, version, hashes, and App Installer policy before changing GitHub. It creates and verifies a draft release before publishing it as latest. A conflicting tag, asset, run, signer, or modified candidate fails closed.

Do not create version tags manually, replace published assets, or prepare an older run when a newer successful run exists. If a production-feed check fails after publication, publish a newer release.

## Build an unsigned bundle manually

Use a new empty output root. The build script restores and publishes x64 and ARM64 in isolated parallel workers, then the staging script validates both package graphs before creating the bundle and App Installer feed:

```powershell
$outputRoot = Join-Path $PWD 'artifacts\manual-unsigned'
$packageOutput = Join-Path $outputRoot 'packages'
[xml]$manifest = Get-Content `
  -LiteralPath .\src\PackagePilot.App\Package.appxmanifest `
  -Raw
$packageVersion = $manifest.SelectSingleNode(
  "/*[local-name()='Package']/*[local-name()='Identity']").GetAttribute('Version')

pwsh -NoProfile -File .\build\Build-UnsignedPackages.ps1 `
  -ArtifactsRoot (Join-Path $outputRoot 'build') `
  -PackageOutputRoot $packageOutput `
  -LogDirectory (Join-Path $outputRoot 'logs')

pwsh -NoProfile -File .\build\Stage-UnsignedRelease.ps1 `
  -PackageOutputDirectory $packageOutput `
  -StagingDirectory (Join-Path $outputRoot 'staging') `
  -Version $packageVersion `
  -Repository 'nativepapaya/package-pilot'
```

For local testing, `build\New-DevelopmentCertificate.ps1` creates a non-exportable development signing key and trusts its public certificate in the machine's `TrustedPeople` store. This is a persistent, machine-wide trust change; review the script before running it and remove the certificate when testing is complete.
