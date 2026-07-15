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

function Assert-PowerShellParses {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        (Resolve-Path -LiteralPath $Path).Path,
        [ref]$tokens,
        [ref]$errors)
    if ($errors.Count -ne 0) {
        throw "'$Path' has PowerShell parse errors: $($errors.Message -join '; ')"
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$appProjectPath = Join-Path $repositoryRoot 'src\PackagePilot.App\PackagePilot.App.csproj'
$arm64ProfilePath = Join-Path $repositoryRoot 'src\PackagePilot.App\Properties\PublishProfiles\win-arm64.pubxml'
$solutionPath = Join-Path $repositoryRoot 'PackagePilot.slnx'
$workflowPath = Join-Path $repositoryRoot '.github\workflows\release.yml'
$ciPath = Join-Path $repositoryRoot '.github\workflows\ci.yml'
$bundleScriptPath = Join-Path $repositoryRoot 'build\New-MsixBundle.ps1'

[xml]$appProject = Get-Content -LiteralPath $appProjectPath -Raw
$platforms = $appProject.SelectSingleNode('/Project/PropertyGroup/Platforms')
Assert-True -Condition ($null -ne $platforms -and $platforms.InnerText -eq 'x64;ARM64') `
    -Message 'The app project must declare exactly x64 and ARM64 platforms.'

$platformTargets = @($appProject.SelectNodes('/Project/PropertyGroup/PlatformTarget'))
Assert-True -Condition (
    $platformTargets.Count -eq 2 -and
    @($platformTargets | Where-Object {
        $_.InnerText -eq 'x64' -and $_.GetAttribute('Condition') -match "!= 'ARM64'"
    }).Count -eq 1 -and
    @($platformTargets | Where-Object {
        $_.InnerText -eq 'ARM64' -and $_.GetAttribute('Condition') -match "== 'ARM64'"
    }).Count -eq 1) `
    -Message 'PlatformTarget must map x64 and ARM64 explicitly.'

$runtimeIdentifiers = @($appProject.SelectNodes('/Project/PropertyGroup/RuntimeIdentifier'))
Assert-True -Condition (
    @($runtimeIdentifiers | Where-Object { $_.InnerText -eq 'win-x64' }).Count -eq 1 -and
    @($runtimeIdentifiers | Where-Object { $_.InnerText -eq 'win-arm64' }).Count -eq 1) `
    -Message 'The app project must map both Windows runtime identifiers.'

$companionReferences = @($appProject.SelectNodes(
    "/Project/ItemGroup/ProjectReference[contains(@Include, 'PackagePilot.Background') or contains(@Include, 'PackagePilot.SourceAdmin')]"))
Assert-True -Condition (
    $companionReferences.Count -eq 2 -and
    @($companionReferences | Where-Object {
        $_.GetAttribute('ReferenceOutputAssembly') -eq 'false' -and
        $_.GetAttribute('Private') -eq 'false'
    }).Count -eq 2) `
    -Message 'Companion executables must be build-only references with publish traversal disabled.'

$packagedCompanionLinks = @($appProject.SelectNodes('/Project/ItemGroup/Content/@Link') |
    ForEach-Object Value |
    Where-Object { $_ -match '^PackagePilot\.(Background|SourceAdmin)\.' })
foreach ($requiredPayload in @(
    'PackagePilot.Background.exe'
    'PackagePilot.Background.dll'
    'PackagePilot.Background.deps.json'
    'PackagePilot.Background.runtimeconfig.json'
    'PackagePilot.Background.pri'
    'PackagePilot.SourceAdmin.exe'
    'PackagePilot.SourceAdmin.dll'
    'PackagePilot.SourceAdmin.deps.json'
    'PackagePilot.SourceAdmin.runtimeconfig.json'
)) {
    Assert-True -Condition ($packagedCompanionLinks -contains $requiredPayload) `
        -Message "The package payload is missing '$requiredPayload'."
}

[xml]$arm64Profile = Get-Content -LiteralPath $arm64ProfilePath -Raw
Assert-True -Condition (
    $arm64Profile.Project.PropertyGroup.Platform -eq 'ARM64' -and
    $arm64Profile.Project.PropertyGroup.RuntimeIdentifier -eq 'win-arm64' -and
    $arm64Profile.Project.PropertyGroup.SelfContained -eq 'false') `
    -Message 'The ARM64 publish profile must be framework-dependent and target win-arm64.'

[xml]$solution = Get-Content -LiteralPath $solutionPath -Raw
$solutionProjects = @($solution.SelectNodes('/Solution/Folder/Project') | ForEach-Object Path)
Assert-True -Condition (
    $solutionProjects -contains 'src/PackagePilot.Background/PackagePilot.Background.csproj' -and
    $solutionProjects -contains 'src/PackagePilot.SourceAdmin/PackagePilot.SourceAdmin.csproj') `
    -Message 'Both packaged companion projects must participate in solution validation.'

$workflow = Get-Content -LiteralPath $workflowPath -Raw
$ci = Get-Content -LiteralPath $ciPath -Raw
foreach ($requiredText in @(
    'win-x64'
    'win-arm64'
    'PackagePilot.msixbundle'
    'Microsoft.WindowsAppRuntime.2.x64.msix'
    'Microsoft.WindowsAppRuntime.2.arm64.msix'
    'New-MsixBundle.ps1'
    'Assert-PackagePayload'
    'Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll'
)) {
    Assert-True -Condition ($workflow.Contains($requiredText)) `
        -Message "Release workflow is missing '$requiredText'."
}
Assert-True -Condition ($ci.Contains('-p:Platform=ARM64') -and $ci.Contains('win-arm64')) `
    -Message 'CI must compile the ARM64 application graph.'

Assert-PowerShellParses -Path $bundleScriptPath

Write-Output 'Packaging architecture tests passed.'
