#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter()]
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),

    [Parameter(Mandatory)]
    [string]$ArtifactsRoot,

    [Parameter(Mandatory)]
    [string]$PackageOutputRoot,

    [Parameter(Mandatory)]
    [string]$LogDirectory,

    [Parameter()]
    [string]$DotNetPath = 'dotnet',

    [Parameter()]
    [string]$NativeCommandPath = (Join-Path $PSScriptRoot 'NativeCommand.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-UnwrittenPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Assert-EmptyDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            throw "$Description path must be a directory: '$Path'."
        }

        if ($null -ne (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1)) {
            throw "$Description directory must be empty to prevent stale release payloads: '$Path'."
        }
    }
}

$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$resolvedArtifactsRoot = Resolve-UnwrittenPath -Path $ArtifactsRoot
$resolvedPackageOutputRoot = Resolve-UnwrittenPath -Path $PackageOutputRoot
$resolvedLogDirectory = Resolve-UnwrittenPath -Path $LogDirectory
$resolvedNativeCommandPath = (Resolve-Path -LiteralPath $NativeCommandPath).Path
$appProjectPath = Join-Path $resolvedRepositoryRoot 'src\PackagePilot.App\PackagePilot.App.csproj'

if (-not (Test-Path -LiteralPath $appProjectPath -PathType Leaf)) {
    throw "Package Pilot app project was not found: '$appProjectPath'."
}

if ($resolvedArtifactsRoot -eq $resolvedPackageOutputRoot -or
    $resolvedArtifactsRoot -eq $resolvedLogDirectory -or
    $resolvedPackageOutputRoot -eq $resolvedLogDirectory) {
    throw 'ArtifactsRoot, PackageOutputRoot, and LogDirectory must be distinct directories.'
}

$builds = @(
    [pscustomobject]@{
        Architecture = 'x64'
        Platform = 'x64'
        Runtime = 'win-x64'
        Order = 0
    }
    [pscustomobject]@{
        Architecture = 'arm64'
        Platform = 'ARM64'
        Runtime = 'win-arm64'
        Order = 1
    }
)

foreach ($build in $builds) {
    Assert-EmptyDirectory `
        -Path (Join-Path $resolvedArtifactsRoot $build.Architecture) `
        -Description "$($build.Platform) artifacts"
    Assert-EmptyDirectory `
        -Path (Join-Path $resolvedPackageOutputRoot $build.Architecture) `
        -Description "$($build.Platform) package output"
}
Assert-EmptyDirectory -Path $resolvedLogDirectory -Description 'Build log'

[IO.Directory]::CreateDirectory($resolvedArtifactsRoot) | Out-Null
[IO.Directory]::CreateDirectory($resolvedPackageOutputRoot) | Out-Null
[IO.Directory]::CreateDirectory($resolvedLogDirectory) | Out-Null

$parallelStopwatch = [Diagnostics.Stopwatch]::StartNew()
$results = @(
    $builds | ForEach-Object -Parallel {
        $build = $_
        $stopwatch = $null
        $succeeded = $false
        $failureMessage = $null
        $artifactsPath = $null
        $packageOutputPath = $null
        $logPath = $null

        try {
            $ErrorActionPreference = 'Stop'
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            $projectPath = $using:appProjectPath
            $artifactsRoot = $using:resolvedArtifactsRoot
            $packageOutputRoot = $using:resolvedPackageOutputRoot
            $logDirectory = $using:resolvedLogDirectory
            $nativeCommandPath = $using:resolvedNativeCommandPath
            $dotNetPath = $using:DotNetPath

            $artifactsPath = Join-Path $artifactsRoot $build.Architecture
            $packageOutputPath = Join-Path $packageOutputRoot $build.Architecture
            $logPath = Join-Path $logDirectory "$($build.Architecture).log"
            $packageOutputWithSeparator = $packageOutputPath + [IO.Path]::DirectorySeparatorChar

            [IO.Directory]::CreateDirectory($artifactsPath) | Out-Null
            [IO.Directory]::CreateDirectory($packageOutputPath) | Out-Null
            . $nativeCommandPath

            "Package Pilot unsigned $($build.Platform) package build" |
                Set-Content -LiteralPath $logPath -Encoding utf8

            $restoreArguments = @(
                'restore'
                $projectPath
                '--runtime'
                $build.Runtime
                '--artifacts-path'
                $artifactsPath
                "-p:Platform=$($build.Platform)"
                '-maxcpucount:1'
            )
            Invoke-NativeChecked `
                -FilePath $dotNetPath `
                -ArgumentList $restoreArguments `
                -Activity "Restore $($build.Platform) package graph" `
                -OutputMode Stream *>&1 |
                Out-File -LiteralPath $logPath -Encoding utf8 -Append

            $publishArguments = @(
                'publish'
                $projectPath
                '--configuration'
                'Release'
                '--runtime'
                $build.Runtime
                '--self-contained'
                'false'
                '--no-restore'
                '--artifacts-path'
                $artifactsPath
                "-p:Platform=$($build.Platform)"
                '-p:GenerateAppxPackageOnBuild=true'
                '-p:AppxPackageSigningEnabled=false'
                '-p:AppxBundle=Never'
                '-p:AppxSymbolPackageEnabled=false'
                '-p:UapAppxPackageBuildMode=SideloadOnly'
                '-p:WindowsAppSDKSelfContained=false'
                "-p:AppxPackageDir=$packageOutputWithSeparator"
                '-maxcpucount:1'
            )
            Invoke-NativeChecked `
                -FilePath $dotNetPath `
                -ArgumentList $publishArguments `
                -Activity "Publish unsigned $($build.Platform) MSIX" `
                -OutputMode Stream *>&1 |
                Out-File -LiteralPath $logPath -Encoding utf8 -Append

            $succeeded = $true
        }
        catch {
            $failureMessage = $_.Exception.Message
            if (-not [string]::IsNullOrWhiteSpace($logPath)) {
                try {
                    "ERROR: $failureMessage" |
                        Out-File -LiteralPath $logPath -Encoding utf8 -Append
                }
                catch {
                    $failureMessage += " The worker log could not be written: $($_.Exception.Message)"
                }
            }
        }
        finally {
            if ($null -ne $stopwatch) {
                $stopwatch.Stop()
            }
        }

        [pscustomobject]@{
            Architecture = $build.Architecture
            Platform = $build.Platform
            Runtime = $build.Runtime
            Succeeded = $succeeded
            ElapsedMilliseconds = if ($null -eq $stopwatch) { 0 } else { $stopwatch.ElapsedMilliseconds }
            ArtifactsPath = $artifactsPath
            PackageOutputPath = $packageOutputPath
            LogPath = $logPath
            Error = $failureMessage
            Order = $build.Order
        }
    } -ThrottleLimit 2
)
$parallelStopwatch.Stop()

$orderedResults = @($results | Sort-Object Order)
$orderedResults | Write-Output

$resultArchitectures = @($orderedResults | ForEach-Object Architecture)
$hasCompleteResultSet =
    $orderedResults.Count -eq 2 -and
    @($resultArchitectures | Sort-Object -Unique).Count -eq 2 -and
    $resultArchitectures -contains 'x64' -and
    $resultArchitectures -contains 'arm64'

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $summaryLines = @(
        '### Unsigned package build'
        ''
        '| Architecture | Result | Duration | Log |'
        '| --- | --- | ---: | --- |'
    )
    foreach ($result in $orderedResults) {
        $status = if ($result.Succeeded) { 'Succeeded' } else { 'Failed' }
        $summaryLines += "| $($result.Architecture) | $status | $($result.ElapsedMilliseconds) ms | $([IO.Path]::GetFileName($result.LogPath)) |"
    }
    $summaryLines += ''
    $summaryLines += "Parallel build wall time: $($parallelStopwatch.ElapsedMilliseconds) ms."
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ($summaryLines -join [Environment]::NewLine)
}

if (-not $hasCompleteResultSet) {
    throw "Unsigned package build returned an incomplete architecture result set after all workers finished: '$($resultArchitectures -join ',')'."
}

$failures = @($orderedResults | Where-Object { -not $_.Succeeded })
if ($failures.Count -ne 0) {
    $failureSummary = $failures |
        ForEach-Object { "$($_.Platform): $($_.Error) (log: $($_.LogPath))" }
    throw "Unsigned package build failed after both architecture workers completed. $($failureSummary -join '; ')"
}
