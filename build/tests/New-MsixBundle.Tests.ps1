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

function Find-MakeAppx {
    $command = Get-Command 'makeappx.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $roots = @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\bin')
        (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools')
    )
    $candidates = foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root -PathType Container) {
            Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' }
        }
    }
    $selected = @($candidates) | Sort-Object FullName -Descending | Select-Object -First 1
    if ($null -eq $selected) {
        throw 'MakeAppx.exe was not found for the MSIX bundle test.'
    }
    return $selected.FullName
}

function New-TestPackage {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('x64', 'arm64')]
        [string]$Architecture,

        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$MakeAppx
    )

    $contentDirectory = Join-Path $Root "content-$Architecture"
    $assetsDirectory = Join-Path $contentDirectory 'Assets'
    [void][IO.Directory]::CreateDirectory($assetsDirectory)

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="PackagePilot.Desktop" Publisher="CN=PackagePilot.Dev" Version="9.8.7.6" ProcessorArchitecture="$Architecture" />
  <Properties>
    <DisplayName>Package Pilot bundle test</DisplayName>
    <PublisherDisplayName>Package Pilot</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Resources><Resource Language="en-us" /></Resources>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Applications>
    <Application Id="App" Executable="PackagePilot.App.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Package Pilot" Description="Bundle test" BackgroundColor="transparent"
                          Square150x150Logo="Assets\Square150x150Logo.png"
                          Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@
    [IO.File]::WriteAllText(
        (Join-Path $contentDirectory 'AppxManifest.xml'),
        $manifest,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes((Join-Path $contentDirectory 'PackagePilot.App.exe'), [byte[]](0x4d, 0x5a))

    $png = [Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=')
    foreach ($assetName in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
        [IO.File]::WriteAllBytes((Join-Path $assetsDirectory $assetName), $png)
    }

    $packagePath = Join-Path $Root "PackagePilot.$Architecture.msix"
    & $MakeAppx 'pack' '/o' '/d' $contentDirectory '/p' $packagePath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx pack failed for $Architecture with exit code $LASTEXITCODE."
    }
    return $packagePath
}

$bundleScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\New-MsixBundle.ps1')).Path
$makeAppx = Find-MakeAppx
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testRoot = Join-Path $temporaryRoot "PackagePilot-BundleTest-$([Guid]::NewGuid().ToString('N'))"
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
Assert-True -Condition $resolvedTestRoot.StartsWith(
    $temporaryRoot + '\',
    [StringComparison]::OrdinalIgnoreCase) -Message 'Unsafe test temporary path.'

try {
    [void][IO.Directory]::CreateDirectory($resolvedTestRoot)
    $x64Package = New-TestPackage -Architecture x64 -Root $resolvedTestRoot -MakeAppx $makeAppx
    $arm64Package = New-TestPackage -Architecture arm64 -Root $resolvedTestRoot -MakeAppx $makeAppx
    $bundlePath = Join-Path $resolvedTestRoot 'PackagePilot.msixbundle'

    & $bundleScript `
        -X64PackagePath $x64Package `
        -Arm64PackagePath $arm64Package `
        -OutputPath $bundlePath `
        -MakeAppxPath $makeAppx | Out-Null
    Assert-True -Condition (Test-Path -LiteralPath $bundlePath -PathType Leaf) `
        -Message 'The bundle script did not produce PackagePilot.msixbundle.'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
    try {
        Assert-True -Condition ($null -eq $archive.GetEntry('AppxSignature.p7x')) `
            -Message 'The hosted bundle output must remain unsigned.'
        $manifestEntry = $archive.GetEntry('AppxMetadata/AppxBundleManifest.xml')
        Assert-True -Condition ($null -ne $manifestEntry) -Message 'The bundle manifest is missing.'
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
        $identity = $manifest.SelectSingleNode("/*[local-name()='Bundle']/*[local-name()='Identity']")
        Assert-True -Condition ($identity.GetAttribute('Name') -eq 'PackagePilot.Desktop') `
            -Message 'Bundle identity name mismatch.'
        Assert-True -Condition ($identity.GetAttribute('Publisher') -eq 'CN=PackagePilot.Dev') `
            -Message 'Bundle identity publisher mismatch.'
        Assert-True -Condition ($identity.GetAttribute('Version') -eq '9.8.7.6') `
            -Message 'Bundle identity version mismatch.'
        $architectures = @($manifest.SelectNodes(
            "/*[local-name()='Bundle']/*[local-name()='Packages']/*[local-name()='Package']") |
            ForEach-Object { $_.GetAttribute('Architecture').ToLowerInvariant() } |
            Sort-Object -Unique)
        Assert-True -Condition (($architectures -join ',') -eq 'arm64,x64') `
            -Message "Bundle architecture set mismatch: '$($architectures -join ',')'."
    }
    finally {
        $archive.Dispose()
    }

    $duplicateArchitectureWasRejected = $false
    try {
        & $bundleScript `
            -X64PackagePath $x64Package `
            -Arm64PackagePath $x64Package `
            -OutputPath (Join-Path $resolvedTestRoot 'invalid.msixbundle') `
            -MakeAppxPath $makeAppx | Out-Null
    }
    catch {
        $duplicateArchitectureWasRejected = $true
    }
    Assert-True -Condition $duplicateArchitectureWasRejected `
        -Message 'A duplicate architecture input was not rejected.'

    $swappedInputsWereRejected = $false
    try {
        & $bundleScript `
            -X64PackagePath $arm64Package `
            -Arm64PackagePath $x64Package `
            -OutputPath (Join-Path $resolvedTestRoot 'swapped.msixbundle') `
            -MakeAppxPath $makeAppx | Out-Null
    }
    catch {
        $swappedInputsWereRejected = $true
    }
    Assert-True -Condition $swappedInputsWereRejected `
        -Message 'Swapped architecture inputs were not rejected.'
}
finally {
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

Write-Output 'New-MsixBundle tests passed.'
