#requires -Version 7.0

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

function Invoke-TestBuild {
    param(
        [Parameter(Mandatory)]
        [string]$TestRoot,

        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$BuildScriptPath,

        [Parameter(Mandatory)]
        [string]$NativeCommandPath
    )

    return @(
        & $BuildScriptPath `
            -RepositoryRoot $RepositoryRoot `
            -ArtifactsRoot (Join-Path $TestRoot 'artifacts') `
            -PackageOutputRoot (Join-Path $TestRoot 'packages') `
            -LogDirectory (Join-Path $TestRoot 'logs') `
            -DotNetPath 'fake-dotnet' `
            -NativeCommandPath $NativeCommandPath
    )
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$buildScriptPath = Join-Path $repositoryRoot 'build\Build-UnsignedPackages.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "PackagePilot-parallel-build-$([Guid]::NewGuid().ToString('N'))"
$nativeCommandPath = Join-Path $testRoot 'NativeCommand.ps1'
$brokenNativeCommandPath = Join-Path $testRoot 'BrokenNativeCommand.ps1'
$appProjectPath = Join-Path $repositoryRoot 'src\PackagePilot.App\PackagePilot.App.csproj'
$backgroundProjectPath = Join-Path $repositoryRoot 'src\PackagePilot.Background\PackagePilot.Background.csproj'
$dotnetPath = Join-Path $repositoryRoot '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    $dotnetPath = 'dotnet'
}

$fakeNativeCommand = @'
function Invoke-NativeChecked {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList,
        [Parameter(Mandatory)][string]$Activity,
        [Parameter(Mandatory)][ValidateSet('Stream', 'Capture', 'Discard')][string]$OutputMode,
        [Parameter()][string[]]$RedactedValues = @()
    )

    Write-Output "activity=$Activity"
    Write-Output "file=$FilePath"
    Write-Output "arguments=$($ArgumentList -join '|')"

    $isArm64Publish =
        $ArgumentList[0] -eq 'publish' -and
        $ArgumentList -contains '-p:Platform=ARM64'
    if ($env:PACKAGEPILOT_TEST_FAIL_ARM64 -eq '1' -and $isArm64Publish) {
        throw 'Simulated ARM64 publish failure.'
    }
}
'@

try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    [IO.File]::WriteAllText(
        $nativeCommandPath,
        $fakeNativeCommand,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $brokenNativeCommandPath,
        "throw 'Simulated worker setup failure.'",
        [Text.UTF8Encoding]::new($false))

    $scriptText = Get-Content -LiteralPath $buildScriptPath -Raw
    Assert-True -Condition ($scriptText.Contains('ForEach-Object -Parallel')) `
        -Message 'The package builds must use PowerShell 7 parallel runspaces.'
    Assert-True -Condition ($scriptText.Contains('-ThrottleLimit 2')) `
        -Message 'Parallel package builds must be limited to two workers.'
    Assert-True -Condition ($scriptText.Contains('Parallel build wall time')) `
        -Message 'Parallel package builds must report their wall time for regression tracking.'

    $x64EvaluationRoot = Join-Path $testRoot 'evaluated-x64'
    $arm64EvaluationRoot = Join-Path $testRoot 'evaluated-arm64'
    $x64AppProperties = & $dotnetPath msbuild $appProjectPath `
        -nologo `
        -p:Configuration=Release `
        -p:Platform=x64 `
        -p:RuntimeIdentifier=win-x64 `
        -p:UseArtifactsOutput=true `
        "-p:ArtifactsPath=$x64EvaluationRoot" `
        -getProperty:BackgroundOutputDirectory,PublishDir,ProjectAssetsFile |
        ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild property evaluation failed for x64 with exit code $LASTEXITCODE."
    }

    $x64BackgroundOutput = & $dotnetPath msbuild $backgroundProjectPath `
        -nologo `
        -p:Configuration=Release `
        -p:Platform=x64 `
        -p:RuntimeIdentifier=win-x64 `
        -p:UseArtifactsOutput=true `
        "-p:ArtifactsPath=$x64EvaluationRoot" `
        -getProperty:OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild companion property evaluation failed for x64 with exit code $LASTEXITCODE."
    }

    $arm64AppProperties = & $dotnetPath msbuild $appProjectPath `
        -nologo `
        -p:Configuration=Release `
        -p:Platform=ARM64 `
        -p:RuntimeIdentifier=win-arm64 `
        -p:UseArtifactsOutput=true `
        "-p:ArtifactsPath=$arm64EvaluationRoot" `
        -getProperty:BackgroundOutputDirectory,PublishDir,ProjectAssetsFile |
        ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild property evaluation failed for ARM64 with exit code $LASTEXITCODE."
    }

    Assert-True -Condition (
        [IO.Path]::GetFullPath($x64AppProperties.Properties.BackgroundOutputDirectory) -eq
        [IO.Path]::GetFullPath(($x64BackgroundOutput -join [Environment]::NewLine))) `
        -Message 'The packaged background-host path must match its artifacts output path.'
    Assert-True -Condition (
        $x64AppProperties.Properties.PublishDir.StartsWith(
            $x64EvaluationRoot,
            [StringComparison]::OrdinalIgnoreCase)) `
        -Message 'The x64 publish directory must remain inside its artifacts root.'
    Assert-True -Condition (
        $arm64AppProperties.Properties.PublishDir.StartsWith(
            $arm64EvaluationRoot,
            [StringComparison]::OrdinalIgnoreCase)) `
        -Message 'The ARM64 publish directory must remain inside its artifacts root.'
    Assert-True -Condition (
        $x64AppProperties.Properties.ProjectAssetsFile -ne
        $arm64AppProperties.Properties.ProjectAssetsFile) `
        -Message 'x64 and ARM64 restores must not share project.assets.json.'

    $successRoot = Join-Path $testRoot 'success'
    $successResults = Invoke-TestBuild `
        -TestRoot $successRoot `
        -RepositoryRoot $repositoryRoot `
        -BuildScriptPath $buildScriptPath `
        -NativeCommandPath $nativeCommandPath

    Assert-True -Condition ($successResults.Count -eq 2) `
        -Message "Expected two successful architecture results, found $($successResults.Count)."
    Assert-True -Condition (@($successResults | Where-Object Succeeded).Count -eq 2) `
        -Message 'Both architecture results must report success.'
    Assert-True -Condition (($successResults.Architecture -join ',') -eq 'x64,arm64') `
        -Message 'Architecture results must be returned in deterministic x64, ARM64 order.'

    foreach ($result in $successResults) {
        Assert-True -Condition ($result.ElapsedMilliseconds -ge 0) `
            -Message "$($result.Platform) did not report elapsed time."
        Assert-True -Condition (Test-Path -LiteralPath $result.LogPath -PathType Leaf) `
            -Message "$($result.Platform) did not create its build log."

        $log = Get-Content -LiteralPath $result.LogPath -Raw
        $expectedArtifactsPath = Join-Path (Join-Path $successRoot 'artifacts') $result.Architecture
        Assert-True -Condition ($log.Contains("restore|$repositoryRoot\src\PackagePilot.App\PackagePilot.App.csproj")) `
            -Message "$($result.Platform) did not restore the app project."
        Assert-True -Condition ($log.Contains("--artifacts-path|$expectedArtifactsPath")) `
            -Message "$($result.Platform) did not use its isolated artifacts root."
        Assert-True -Condition ($log.Contains("-p:Platform=$($result.Platform)")) `
            -Message "$($result.Platform) did not pass the expected platform property."
        Assert-True -Condition ($log.Contains('-maxcpucount:1')) `
            -Message "$($result.Platform) did not bound MSBuild concurrency."

        $restoreIndex = $log.IndexOf('arguments=restore|', [StringComparison]::Ordinal)
        $publishIndex = $log.IndexOf('arguments=publish|', [StringComparison]::Ordinal)
        Assert-True -Condition ($restoreIndex -ge 0 -and $publishIndex -gt $restoreIndex) `
            -Message "$($result.Platform) must restore before publishing."

        $publishArguments = $log.Substring($publishIndex)
        Assert-True -Condition ($publishArguments.Contains('--no-restore')) `
            -Message "$($result.Platform) publish must reuse its completed restore."
    }

    $failureRoot = Join-Path $testRoot 'failure'
    $failureResults = [Collections.Generic.List[object]]::new()
    $failureThrown = $false
    $env:PACKAGEPILOT_TEST_FAIL_ARM64 = '1'
    try {
        & $buildScriptPath `
            -RepositoryRoot $repositoryRoot `
            -ArtifactsRoot (Join-Path $failureRoot 'artifacts') `
            -PackageOutputRoot (Join-Path $failureRoot 'packages') `
            -LogDirectory (Join-Path $failureRoot 'logs') `
            -DotNetPath 'fake-dotnet' `
            -NativeCommandPath $nativeCommandPath |
            ForEach-Object { $failureResults.Add($_) }
    }
    catch {
        $failureThrown = $true
        Assert-True -Condition ($_.Exception.Message.Contains('after both architecture workers completed')) `
            -Message 'The parent failure must state that both workers completed.'
    }
    finally {
        Remove-Item Env:PACKAGEPILOT_TEST_FAIL_ARM64 -ErrorAction SilentlyContinue
    }

    Assert-True -Condition $failureThrown `
        -Message 'A failed architecture publish must fail the parent build.'
    Assert-True -Condition ($failureResults.Count -eq 2) `
        -Message 'The parent must emit both structured results before reporting failure.'
    Assert-True -Condition (@($failureResults | Where-Object Succeeded).Count -eq 1) `
        -Message 'The unaffected architecture must be allowed to complete successfully.'
    Assert-True -Condition (@($failureResults | Where-Object { -not $_.Succeeded }).Count -eq 1) `
        -Message 'Exactly one architecture must report the simulated failure.'

    $failedResult = @($failureResults | Where-Object { -not $_.Succeeded })[0]
    Assert-True -Condition ($failedResult.Architecture -eq 'arm64') `
        -Message 'The simulated ARM64 failure was attributed to the wrong architecture.'
    Assert-True -Condition ($failedResult.Error -eq 'Simulated ARM64 publish failure.') `
        -Message 'The structured result did not preserve the native-command failure message.'

    $setupFailureRoot = Join-Path $testRoot 'setup-failure'
    $setupFailureResults = [Collections.Generic.List[object]]::new()
    $setupFailureThrown = $false
    try {
        & $buildScriptPath `
            -RepositoryRoot $repositoryRoot `
            -ArtifactsRoot (Join-Path $setupFailureRoot 'artifacts') `
            -PackageOutputRoot (Join-Path $setupFailureRoot 'packages') `
            -LogDirectory (Join-Path $setupFailureRoot 'logs') `
            -DotNetPath 'fake-dotnet' `
            -NativeCommandPath $brokenNativeCommandPath |
            ForEach-Object { $setupFailureResults.Add($_) }
    }
    catch {
        $setupFailureThrown = $true
    }
    Assert-True -Condition $setupFailureThrown `
        -Message 'A worker setup failure must fail the parent build.'
    Assert-True -Condition (
        $setupFailureResults.Count -eq 2 -and
        @($setupFailureResults | Where-Object { -not $_.Succeeded }).Count -eq 2 -and
        @($setupFailureResults | Where-Object {
            $_.Error -eq 'Simulated worker setup failure.'
        }).Count -eq 2) `
        -Message 'Worker setup failures must still return one structured result per architecture.'

    Write-Output 'Parallel package build tests passed.'
}
finally {
    Remove-Item Env:PACKAGEPILOT_TEST_FAIL_ARM64 -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
