#Requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ThrowsContaining {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string[]]$ExpectedText,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    try {
        & $Action
    }
    catch {
        foreach ($expected in $ExpectedText) {
            Assert-True -Condition $_.Exception.Message.Contains($expected, [StringComparison]::Ordinal) `
                -Message "$FailureMessage Missing text: '$expected'. Actual: $($_.Exception.Message)"
        }

        return
    }

    throw "$FailureMessage No exception was thrown."
}

function Add-ZipEntry {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter()]
        [string]$Content = ''
    )

    $entry = $Archive.CreateEntry($Name, [IO.Compression.CompressionLevel]::NoCompression)
    $stream = $entry.Open()
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Content)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally {
        $stream.Dispose()
    }
}

function New-TestMsix {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Publisher,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Architecture,

        [Parameter(Mandatory)]
        [bool]$HasSignature,

        [Parameter()]
        [string[]]$PayloadEntries = @()
    )

    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    $archive = [IO.Compression.ZipFile]::Open(
        $Path,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="$Name" Publisher="$Publisher" Version="$Version" ProcessorArchitecture="$Architecture" />
</Package>
"@
        Add-ZipEntry -Archive $archive -Name 'AppxManifest.xml' -Content $manifest
        foreach ($entryName in $PayloadEntries) {
            Add-ZipEntry -Archive $archive -Name $entryName
        }
        if ($HasSignature) {
            Add-ZipEntry -Archive $archive -Name 'AppxSignature.p7x' -Content 'test signature'
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-ValidPackageFixtures {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string[]]$RequiredPayload,

        [Parameter()]
        [hashtable]$Overrides = @{}
    )

    foreach ($architecture in @('x64', 'arm64')) {
        $mainName = 'PackagePilot.Desktop'
        $mainPublisher = 'CN=PackagePilot.Dev'
        $mainVersion = '1.2.3.4'
        $mainSignature = $false
        $runtimeName = 'Microsoft.WindowsAppRuntime.2'
        $runtimePublisher = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        $runtimeVersion = '2.2.0.0'
        $runtimeSignature = $true
        $mainPayload = @($RequiredPayload)

        if ($architecture -eq 'x64') {
            if ($Overrides.ContainsKey('MainName')) { $mainName = [string]$Overrides.MainName }
            if ($Overrides.ContainsKey('MainPublisher')) { $mainPublisher = [string]$Overrides.MainPublisher }
            if ($Overrides.ContainsKey('MainVersion')) { $mainVersion = [string]$Overrides.MainVersion }
            if ($Overrides.ContainsKey('MainSignature')) { $mainSignature = [bool]$Overrides.MainSignature }
            if ($Overrides.ContainsKey('RuntimeName')) { $runtimeName = [string]$Overrides.RuntimeName }
            if ($Overrides.ContainsKey('RuntimePublisher')) { $runtimePublisher = [string]$Overrides.RuntimePublisher }
            if ($Overrides.ContainsKey('RuntimeVersion')) { $runtimeVersion = [string]$Overrides.RuntimeVersion }
            if ($Overrides.ContainsKey('RuntimeSignature')) { $runtimeSignature = [bool]$Overrides.RuntimeSignature }
            if ($Overrides.ContainsKey('MissingPayload')) {
                $mainPayload = @($RequiredPayload | Where-Object { $_ -ne [string]$Overrides.MissingPayload })
            }
            if ($Overrides.ContainsKey('IncludePowerShellPayload') -and
                [bool]$Overrides.IncludePowerShellPayload) {
                $mainPayload += 'tools/pwsh.exe'
            }
        }

        $architectureRoot = Join-Path $Root $architecture
        New-TestMsix `
            -Path (Join-Path $architectureRoot 'PackagePilot.msix') `
            -Name $mainName `
            -Publisher $mainPublisher `
            -Version $mainVersion `
            -Architecture $architecture `
            -HasSignature $mainSignature `
            -PayloadEntries $mainPayload
        New-TestMsix `
            -Path (Join-Path $architectureRoot 'WindowsAppRuntime.msix') `
            -Name $runtimeName `
            -Publisher $runtimePublisher `
            -Version $runtimeVersion `
            -Architecture $architecture `
            -HasSignature $runtimeSignature
    }
}

function New-Scenario {
    param(
        [Parameter(Mandatory)]
        [string]$TestRoot,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $root = Join-Path $TestRoot $Name
    $packages = Join-Path $root 'packages'
    $staging = Join-Path $root 'staging'
    [IO.Directory]::CreateDirectory($packages) | Out-Null

    return [pscustomobject]@{
        Root = $root
        Packages = $packages
        Staging = $staging
        Log = Join-Path $root 'downstream.log'
    }
}

function Invoke-StageScenario {
    param(
        [Parameter(Mandatory)]
        [string]$StageScriptPath,

        [Parameter(Mandatory)]
        [pscustomobject]$Scenario
    )

    $env:PACKAGEPILOT_STAGE_TEST_LOG = $Scenario.Log
    & $StageScriptPath `
        -PackageOutputDirectory $Scenario.Packages `
        -StagingDirectory $Scenario.Staging `
        -Version '1.2.3.4' `
        -Repository 'example/package-pilot'
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$stageScriptPath = Join-Path $repositoryRoot 'build\Stage-UnsignedRelease.ps1'
$scriptText = Get-Content -LiteralPath $stageScriptPath -Raw
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "PackagePilot-stage-release-$([Guid]::NewGuid().ToString('N'))"
$harnessRoot = Join-Path $testRoot 'harness'

$requiredPayload = @(
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

$bundleStub = @'
param(
    [Parameter(Mandatory)][string]$X64PackagePath,
    [Parameter(Mandatory)][string]$Arm64PackagePath,
    [Parameter(Mandatory)][string]$OutputPath
)
Add-Content -LiteralPath $env:PACKAGEPILOT_STAGE_TEST_LOG -Value 'bundle'
[IO.File]::WriteAllText($OutputPath, 'test bundle', [Text.UTF8Encoding]::new($false))
'@

$appInstallerStub = @'
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$Repository
)
Add-Content -LiteralPath $env:PACKAGEPILOT_STAGE_TEST_LOG -Value 'appinstaller'
[IO.File]::WriteAllText($OutputPath, 'test appinstaller', [Text.UTF8Encoding]::new($false))
'@

try {
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $stageScriptPath,
        [ref]$tokens,
        [ref]$parseErrors)
    $parseErrorMessages = @($parseErrors | ForEach-Object Message)
    Assert-True -Condition ($parseErrors.Count -eq 0) `
        -Message "Stage-UnsignedRelease.ps1 has parse errors: $($parseErrorMessages -join '; ')"
    Assert-True -Condition ($ast.ScriptRequirements.RequiredPSVersion -ge [Version]'7.0') `
        -Message 'Stage-UnsignedRelease.ps1 must require PowerShell 7 or newer.'

    Assert-True -Condition ([regex]::IsMatch(
            $scriptText,
            'ForEach-Object\s+-Parallel\s*\{[\s\S]*?\}\s*-ThrottleLimit\s+2',
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)) `
        -Message 'Unsigned-package inspection must use ForEach-Object -Parallel with ThrottleLimit 2.'

    foreach ($requiredEntry in $requiredPayload) {
        Assert-True -Condition $scriptText.Contains("'$requiredEntry'", [StringComparison]::Ordinal) `
            -Message "Stage-UnsignedRelease.ps1 no longer requires companion payload '$requiredEntry'."
    }

    foreach ($requiredValidation in @(
        "Name -eq 'PackagePilot.Desktop'"
        "Name -eq 'Microsoft.WindowsAppRuntime.2'"
        "Publisher -ne 'CN=PackagePilot.Dev'"
        'Version -ne $using:Version'
        'HasSignature'
        'ForbiddenPowerShellPayload'
        "Publisher -ne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'"
    )) {
        Assert-True -Condition $scriptText.Contains($requiredValidation, [StringComparison]::Ordinal) `
            -Message "Stage-UnsignedRelease.ps1 is missing validation guard '$requiredValidation'."
    }

    $failureGateIndex = $scriptText.IndexOf('if ($failures.Count -gt 0)', [StringComparison]::Ordinal)
    $failureThrowIndex = $scriptText.IndexOf('throw "Unsigned package inspection failed.', [StringComparison]::Ordinal)
    $bundleIndex = $scriptText.IndexOf("'New-MsixBundle.ps1'", [StringComparison]::Ordinal)
    $appInstallerIndex = $scriptText.IndexOf("'New-AppInstaller.ps1'", [StringComparison]::Ordinal)
    Assert-True -Condition (
        $failureGateIndex -ge 0 -and
        $failureThrowIndex -gt $failureGateIndex -and
        $bundleIndex -gt $failureThrowIndex -and
        $appInstallerIndex -gt $bundleIndex) `
        -Message 'Bundle and App Installer creation must remain behind the reconciled inspection-failure gate.'

    [IO.Directory]::CreateDirectory($harnessRoot) | Out-Null
    $harnessStagePath = Join-Path $harnessRoot 'Stage-UnsignedRelease.ps1'
    [IO.File]::WriteAllText($harnessStagePath, $scriptText, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $harnessRoot 'New-MsixBundle.ps1'),
        $bundleStub,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $harnessRoot 'New-AppInstaller.ps1'),
        $appInstallerStub,
        [Text.UTF8Encoding]::new($false))

    $missingArchitectures = New-Scenario -TestRoot $testRoot -Name 'both-fail'
    Assert-ThrowsContaining `
        -Action { Invoke-StageScenario -StageScriptPath $harnessStagePath -Scenario $missingArchitectures } `
        -ExpectedText @(
            'Unsigned package inspection failed.'
            'arm64: Package output directory'
            'x64: Package output directory'
        ) `
        -FailureMessage 'Both architecture failures must be reconciled into one parent failure.'
    Assert-True -Condition (-not (Test-Path -LiteralPath $missingArchitectures.Log)) `
        -Message 'Downstream release creation ran even though both architecture inspections failed.'

    $stale = New-Scenario -TestRoot $testRoot -Name 'stale-staging'
    New-ValidPackageFixtures -Root $stale.Packages -RequiredPayload $requiredPayload
    [IO.Directory]::CreateDirectory($stale.Staging) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $stale.Staging 'unexpected.txt'),
        'stale',
        [Text.UTF8Encoding]::new($false))
    Assert-ThrowsContaining `
        -Action { Invoke-StageScenario -StageScriptPath $harnessStagePath -Scenario $stale } `
        -ExpectedText @('StagingDirectory must be empty to prevent stale release assets') `
        -FailureMessage 'A nonempty staging directory must fail before inspection or downstream creation.'
    Assert-True -Condition (-not (Test-Path -LiteralPath $stale.Log)) `
        -Message 'Downstream release creation ran for a stale staging directory.'

    $success = New-Scenario -TestRoot $testRoot -Name 'success'
    New-ValidPackageFixtures -Root $success.Packages -RequiredPayload $requiredPayload
    Invoke-StageScenario -StageScriptPath $harnessStagePath -Scenario $success
    $downstreamOrder = @(Get-Content -LiteralPath $success.Log)
    Assert-True -Condition (($downstreamOrder -join ',') -eq 'bundle,appinstaller') `
        -Message "Expected bundle then App Installer creation, got '$($downstreamOrder -join ',')'."
    foreach ($assetName in @(
        'PackagePilot.msixbundle'
        'PackagePilot.appinstaller'
        'Microsoft.WindowsAppRuntime.2.x64.msix'
        'Microsoft.WindowsAppRuntime.2.arm64.msix'
    )) {
        Assert-True -Condition (Test-Path -LiteralPath (Join-Path $success.Staging $assetName) -PathType Leaf) `
            -Message "Successful staging did not create '$assetName'."
    }
    $stagedNames = @(
        Get-ChildItem -LiteralPath $success.Staging -Force |
            ForEach-Object Name |
            Sort-Object
    )
    Assert-True `
        -Condition (($stagedNames -join ',') -eq 'Microsoft.WindowsAppRuntime.2.arm64.msix,Microsoft.WindowsAppRuntime.2.x64.msix,PackagePilot.appinstaller,PackagePilot.msixbundle') `
        -Message "Successful staging produced an unexpected asset set: '$($stagedNames -join ',')'."

    $invalidCases = @(
        [pscustomobject]@{
            Name = 'invalid-identity'
            Overrides = @{ MainName = 'PackagePilot.Wrong' }
            Expected = 'Expected one x64 PackagePilot.Desktop MSIX'
        }
        [pscustomobject]@{
            Name = 'invalid-publisher'
            Overrides = @{ MainPublisher = 'CN=Unexpected' }
            Expected = 'identity, publisher, version, or signature state is invalid'
        }
        [pscustomobject]@{
            Name = 'invalid-version'
            Overrides = @{ MainVersion = '9.9.9.9' }
            Expected = 'identity, publisher, version, or signature state is invalid'
        }
        [pscustomobject]@{
            Name = 'signed-main'
            Overrides = @{ MainSignature = $true }
            Expected = 'identity, publisher, version, or signature state is invalid'
        }
        [pscustomobject]@{
            Name = 'unsigned-runtime'
            Overrides = @{ RuntimeSignature = $false }
            Expected = 'Windows App Runtime dependency identity or signature state is invalid'
        }
        [pscustomobject]@{
            Name = 'missing-companion'
            Overrides = @{ MissingPayload = 'PackagePilot.PackageAdmin.exe' }
            Expected = 'missing companion payload: PackagePilot.PackageAdmin.exe'
        }
        [pscustomobject]@{
            Name = 'powershell-runtime'
            Overrides = @{ IncludePowerShellPayload = $true }
            Expected = 'contains forbidden PowerShell runtime payload: tools/pwsh.exe'
        }
    )

    foreach ($case in $invalidCases) {
        $scenario = New-Scenario -TestRoot $testRoot -Name $case.Name
        New-ValidPackageFixtures `
            -Root $scenario.Packages `
            -RequiredPayload $requiredPayload `
            -Overrides $case.Overrides
        Assert-ThrowsContaining `
            -Action { Invoke-StageScenario -StageScriptPath $harnessStagePath -Scenario $scenario } `
            -ExpectedText @($case.Expected) `
            -FailureMessage "The '$($case.Name)' fixture must fail inspection."
        Assert-True -Condition (-not (Test-Path -LiteralPath $scenario.Log)) `
            -Message "The '$($case.Name)' fixture reached bundle or App Installer creation."
    }

    Write-Output 'Unsigned release staging tests passed.'
}
finally {
    Remove-Item Env:PACKAGEPILOT_STAGE_TEST_LOG -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
