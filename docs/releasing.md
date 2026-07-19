# Release and packaging guide

Package Pilot uses GitHub Actions for validation and unsigned packaging, followed by local signing and publication. The signing key never leaves the maintainer's Windows certificate store.

## Release model

Each successful push to `main` runs deterministic tests, assigns a monotonically increasing four-part MSIX version, and retains an unsigned release payload for 30 days. The workflow produces x64 and ARM64 packages and combines them into one bundle.

Public releases use stable asset names so `PackagePilot.appinstaller` can follow the latest release. App Installer checks quietly at most once every 24 hours and also registers its background update task.

The repository currently uses the development identity `PackagePilot.Desktop` / `CN=PackagePilot.Dev`. Do not distribute it broadly until a permanent, publicly trusted publisher is selected. Changing the publisher later creates a new package identity and cannot be delivered as an in-place update.

## Initialize the signing certificate

From an elevated PowerShell window:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\Initialize-ManualReleaseCertificate.ps1
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

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\Publish-ManualRelease.ps1 `
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

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\Publish-ManualRelease.ps1 `
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

Publish one package per architecture:

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

Then combine them:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\build\New-MsixBundle.ps1 `
  -X64PackagePath (Get-ChildItem $packageRoots.x64 -Recurse -Filter '*.msix' | Where-Object Name -Like 'PackagePilot*').FullName `
  -Arm64PackagePath (Get-ChildItem $packageRoots.ARM64 -Recurse -Filter '*.msix' | Where-Object Name -Like 'PackagePilot*').FullName `
  -OutputPath .\artifacts\PackagePilot.msixbundle
```

For local testing, `build\New-DevelopmentCertificate.ps1` creates a non-exportable development signing key and trusts its public certificate in the machine's `TrustedPeople` store. This is a persistent, machine-wide trust change; review the script before running it and remove the certificate when testing is complete.
