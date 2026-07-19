#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageOutputDirectory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$StagingDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidatePattern('^[^/\s]+/[^/\s]+$')]
    [string]$Repository
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedPackageOutput = (Resolve-Path -LiteralPath $PackageOutputDirectory -ErrorAction Stop).Path
if (Test-Path -LiteralPath $StagingDirectory) {
    if (-not (Test-Path -LiteralPath $StagingDirectory -PathType Container)) {
        throw "StagingDirectory must be a directory: '$StagingDirectory'."
    }
    if ($null -ne (Get-ChildItem -LiteralPath $StagingDirectory -Force | Select-Object -First 1)) {
        throw "StagingDirectory must be empty to prevent stale release assets: '$StagingDirectory'."
    }
}
else {
    [IO.Directory]::CreateDirectory($StagingDirectory) | Out-Null
}
$resolvedStagingDirectory = (Resolve-Path -LiteralPath $StagingDirectory -ErrorAction Stop).Path

$requiredMainPackageEntries = @(
    'PackagePilot.Windows.ReadOnly.dll'
    'PackagePilot.Background.exe'
    'PackagePilot.Background.dll'
    'PackagePilot.Background.deps.json'
    'PackagePilot.Background.runtimeconfig.json'
    'PackagePilot.Background.pri'
    'PackagePilot.PackageAdmin.exe'
    'PackagePilot.PackageAdmin.dll'
    'PackagePilot.PackageAdmin.deps.json'
    'PackagePilot.PackageAdmin.runtimeconfig.json'
    'PackagePilot.SourceAdmin.exe'
    'PackagePilot.SourceAdmin.dll'
    'PackagePilot.SourceAdmin.deps.json'
    'PackagePilot.SourceAdmin.runtimeconfig.json'
    'Microsoft.Management.Deployment.CsWinRTProjection.dll'
    'Microsoft.Management.Deployment.dll'
    'Microsoft.Management.Deployment.winmd'
    'Microsoft.Windows.ApplicationModel.Background.Projection.dll'
    'Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll'
)
$forbiddenPowerShellPayloadPattern = '(?i)(^|/)(?:powershell(?:[.]exe)?|pwsh(?:[.](?:exe|dll))?|System[.]Management[.]Automation[.]dll|Microsoft[.]PowerShell[.][^/]+|[^/]+[.](?:ps1|psm1|psd1))$'

$architectures = @(
    [pscustomobject]@{ Architecture = 'x64'; DirectoryName = 'x64' }
    [pscustomobject]@{ Architecture = 'arm64'; DirectoryName = 'arm64' }
)

$inspectionStartedAt = [Diagnostics.Stopwatch]::StartNew()
$inspectionResults = @(
    $architectures | ForEach-Object -Parallel {
        $architecture = $_.Architecture
        $architectureDirectory = Join-Path $using:resolvedPackageOutput $_.DirectoryName
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()

        try {
            if (-not (Test-Path -LiteralPath $architectureDirectory -PathType Container)) {
                throw "Package output directory '$architectureDirectory' does not exist."
            }

            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $packages = @()
            foreach ($package in Get-ChildItem -LiteralPath $architectureDirectory -Recurse -File -Filter '*.msix') {
                $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
                try {
                    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
                    if ($null -eq $manifestEntry) {
                        throw "'$($package.FullName)' does not contain AppxManifest.xml."
                    }

                    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
                    try {
                        [xml]$manifest = $reader.ReadToEnd()
                    }
                    finally {
                        $reader.Dispose()
                    }

                    $identity = $manifest.Package.Identity
                    $identityName = [string]$identity.Name
                    $identityArchitecture = [string]$identity.ProcessorArchitecture
                    $missingPayload = @()
                    $forbiddenPowerShellPayload = @()
                    if ($identityName -eq 'PackagePilot.Desktop' -and
                        $identityArchitecture -eq $architecture) {
                        foreach ($entryName in $using:requiredMainPackageEntries) {
                            if ($null -eq $archive.GetEntry($entryName)) {
                                $missingPayload += $entryName
                            }
                        }
                        $forbiddenPowerShellPayload = @($archive.Entries | Where-Object {
                            $_.FullName -match $using:forbiddenPowerShellPayloadPattern
                        } | ForEach-Object FullName)
                    }

                    $packages += [pscustomobject]@{
                        Path = $package.FullName
                        Name = $identityName
                        Publisher = [string]$identity.Publisher
                        Version = [string]$identity.Version
                        ProcessorArchitecture = $identityArchitecture
                        HasSignature = $null -ne $archive.GetEntry('AppxSignature.p7x')
                        MissingPayload = $missingPayload
                        ForbiddenPowerShellPayload = $forbiddenPowerShellPayload
                    }
                }
                finally {
                    $archive.Dispose()
                }
            }

            $mainPackages = @(
                $packages | Where-Object {
                    $_.Name -eq 'PackagePilot.Desktop' -and
                    $_.ProcessorArchitecture -eq $architecture
                }
            )
            $runtimePackages = @(
                $packages | Where-Object {
                    $_.Name -eq 'Microsoft.WindowsAppRuntime.2' -and
                    $_.ProcessorArchitecture -eq $architecture
                }
            )

            if ($mainPackages.Count -ne 1) {
                throw "Expected one $architecture PackagePilot.Desktop MSIX, but found $($mainPackages.Count)."
            }
            if ($runtimePackages.Count -ne 1) {
                throw "Expected one $architecture Microsoft.WindowsAppRuntime.2 dependency MSIX, but found $($runtimePackages.Count)."
            }

            $mainPackage = $mainPackages[0]
            $runtimePackage = $runtimePackages[0]
            if ($mainPackage.MissingPayload.Count -gt 0) {
                throw "The $architecture Package Pilot package is missing companion payload: $($mainPackage.MissingPayload -join ', ')."
            }
            if ($mainPackage.ForbiddenPowerShellPayload.Count -gt 0) {
                throw "The $architecture Package Pilot package contains forbidden PowerShell runtime payload: $($mainPackage.ForbiddenPowerShellPayload -join ', ')."
            }
            if ($mainPackage.Version -ne $using:Version -or
                $mainPackage.Publisher -ne 'CN=PackagePilot.Dev' -or
                $mainPackage.HasSignature) {
                throw "The $architecture Package Pilot package identity, publisher, version, or signature state is invalid."
            }
            if ($runtimePackage.Version -ne '2.2.0.0' -or
                $runtimePackage.Publisher -ne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -or
                -not $runtimePackage.HasSignature) {
                throw "The $architecture Windows App Runtime dependency identity or signature state is invalid."
            }

            $stopwatch.Stop()
            [pscustomobject]@{
                Architecture = $architecture
                Success = $true
                MainPackagePath = $mainPackage.Path
                RuntimePackagePath = $runtimePackage.Path
                DurationMilliseconds = $stopwatch.ElapsedMilliseconds
                Error = $null
            }
        }
        catch {
            $stopwatch.Stop()
            [pscustomobject]@{
                Architecture = $architecture
                Success = $false
                MainPackagePath = $null
                RuntimePackagePath = $null
                DurationMilliseconds = $stopwatch.ElapsedMilliseconds
                Error = $_.Exception.Message
            }
        }
    } -ThrottleLimit 2
)
$inspectionStartedAt.Stop()

$orderedResults = @($inspectionResults | Sort-Object Architecture)
$resultArchitectures = @($orderedResults | ForEach-Object Architecture)
if ($orderedResults.Count -ne 2 -or
    @($resultArchitectures | Sort-Object -Unique).Count -ne 2 -or
    $resultArchitectures -notcontains 'x64' -or
    $resultArchitectures -notcontains 'arm64') {
    throw "Unsigned package inspection returned an incomplete architecture result set: '$($resultArchitectures -join ',')'."
}
$failures = @($orderedResults | Where-Object { -not $_.Success })
if ($failures.Count -gt 0) {
    $details = $failures | ForEach-Object { "$($_.Architecture): $($_.Error)" }
    throw "Unsigned package inspection failed. $($details -join ' | ')"
}

$payloads = @{}
foreach ($result in $orderedResults) {
    $payloads[$result.Architecture] = $result
    Write-Host ("Inspected {0} package payload in {1} ms." -f
        $result.Architecture,
        $result.DurationMilliseconds)
}

& (Join-Path $PSScriptRoot 'New-MsixBundle.ps1') `
    -X64PackagePath $payloads.x64.MainPackagePath `
    -Arm64PackagePath $payloads.arm64.MainPackagePath `
    -OutputPath (Join-Path $resolvedStagingDirectory 'PackagePilot.msixbundle') | Out-Null

Copy-Item -LiteralPath $payloads.x64.RuntimePackagePath `
    -Destination (Join-Path $resolvedStagingDirectory 'Microsoft.WindowsAppRuntime.2.x64.msix') `
    -Force
Copy-Item -LiteralPath $payloads.arm64.RuntimePackagePath `
    -Destination (Join-Path $resolvedStagingDirectory 'Microsoft.WindowsAppRuntime.2.arm64.msix') `
    -Force

& (Join-Path $PSScriptRoot 'New-AppInstaller.ps1') `
    -Version $Version `
    -OutputPath (Join-Path $resolvedStagingDirectory 'PackagePilot.appinstaller') `
    -Repository $Repository | Out-Null

$expectedStagedNames = @(
    'Microsoft.WindowsAppRuntime.2.arm64.msix'
    'Microsoft.WindowsAppRuntime.2.x64.msix'
    'PackagePilot.appinstaller'
    'PackagePilot.msixbundle'
)
$stagedItems = @(Get-ChildItem -LiteralPath $resolvedStagingDirectory -Force)
$actualStagedNames = @($stagedItems | ForEach-Object Name | Sort-Object)
if ($stagedItems.Count -ne $expectedStagedNames.Count -or
    ($actualStagedNames -join ',') -ne ($expectedStagedNames -join ',') -or
    @($stagedItems | Where-Object { -not $_.PSIsContainer }).Count -ne $expectedStagedNames.Count) {
    throw "The staged unsigned release contains an unexpected file or directory: '$($actualStagedNames -join ',')'."
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $summaryLines = @(
        '### Unsigned package inspection'
        ''
        '| Architecture | Duration |'
        '| --- | ---: |'
    )
    foreach ($result in $orderedResults) {
        $summaryLines += "| $($result.Architecture) | $($result.DurationMilliseconds) ms |"
    }
    $summaryLines += ''
    $summaryLines += "Parallel inspection wall time: $($inspectionStartedAt.ElapsedMilliseconds) ms."
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($summaryLines -join [Environment]::NewLine)
}

Write-Host "Staged unsigned release assets in '$resolvedStagingDirectory'."
