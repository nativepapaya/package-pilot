#Requires -Version 5.1

<#
.SYNOPSIS
Creates Package Pilot's unsigned x64/ARM64 MSIX bundle with the Windows SDK.

.DESCRIPTION
Single-project MSIX produces one package per architecture. This script validates
those packages before asking MakeAppx to combine them, then validates the bundle
manifest. Signing remains an offline/manual release step.

.EXAMPLE
./build/New-MsixBundle.ps1 `
  -X64PackagePath ./artifacts/x64/PackagePilot.msix `
  -Arm64PackagePath ./artifacts/arm64/PackagePilot.msix `
  -OutputPath ./artifacts/PackagePilot.msixbundle
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$X64PackagePath,

    [Parameter(Mandatory)]
    [string]$Arm64PackagePath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [Parameter()]
    [string]$MakeAppxPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-MsixIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $manifestEntry) {
            throw "'$Path' does not contain AppxManifest.xml."
        }

        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $identity = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
        if ($null -eq $identity) {
            throw "'$Path' does not contain a package identity."
        }

        return [pscustomobject]@{
            Name = $identity.GetAttribute('Name')
            Publisher = $identity.GetAttribute('Publisher')
            Version = $identity.GetAttribute('Version')
            Architecture = $identity.GetAttribute('ProcessorArchitecture').ToLowerInvariant()
            HasSignature = $null -ne $archive.GetEntry('AppxSignature.p7x')
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Resolve-MakeAppx {
    if (-not [string]::IsNullOrWhiteSpace($MakeAppxPath)) {
        $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($MakeAppxPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "MakeAppx was not found at '$resolved'."
        }
        return $resolved
    }

    $command = Get-Command 'makeappx.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $searchRoots = @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\bin')
        (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools')
    )
    $candidates = foreach ($root in $searchRoots) {
        if (Test-Path -LiteralPath $root -PathType Container) {
            Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' }
        }
    }

    $selected = @($candidates) | Sort-Object FullName -Descending | Select-Object -First 1
    if ($null -eq $selected) {
        throw 'MakeAppx.exe was not found. Install the Windows 10/11 SDK or restore Microsoft.Windows.SDK.BuildTools.'
    }

    return $selected.FullName
}

function Get-MsixBundleIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.GetEntry('AppxMetadata/AppxBundleManifest.xml')
        if ($null -eq $manifestEntry) {
            throw "'$Path' does not contain AppxMetadata/AppxBundleManifest.xml."
        }

        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $identity = $manifest.SelectSingleNode("/*[local-name()='Bundle']/*[local-name()='Identity']")
        $packages = @($manifest.SelectNodes(
            "/*[local-name()='Bundle']/*[local-name()='Packages']/*[local-name()='Package']"))
        if ($null -eq $identity -or $packages.Count -eq 0) {
            throw "'$Path' contains an incomplete bundle manifest."
        }

        return [pscustomobject]@{
            Name = $identity.GetAttribute('Name')
            Publisher = $identity.GetAttribute('Publisher')
            Version = $identity.GetAttribute('Version')
            Architectures = @($packages | ForEach-Object {
                $_.GetAttribute('Architecture').ToLowerInvariant()
            } | Sort-Object -Unique)
            HasSignature = $null -ne $archive.GetEntry('AppxSignature.p7x')
        }
    }
    finally {
        $archive.Dispose()
    }
}

$resolvedPackages = @(@($X64PackagePath, $Arm64PackagePath) | ForEach-Object {
    $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($_)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Input package '$resolved' was not found."
    }
    if ([IO.Path]::GetExtension($resolved) -ine '.msix') {
        throw "Input package '$resolved' must use the .msix extension."
    }
    $resolved
})
if (($resolvedPackages | Sort-Object -Unique).Count -ne 2) {
    throw 'Two distinct input packages are required.'
}

$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([IO.Path]::GetExtension($resolvedOutput) -ine '.msixbundle') {
    throw "OutputPath must use the .msixbundle extension: '$resolvedOutput'."
}
$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutput)
if (-not [IO.Directory]::Exists($outputDirectory)) {
    [void][IO.Directory]::CreateDirectory($outputDirectory)
}

$packageRecords = @($resolvedPackages | ForEach-Object {
    [pscustomobject]@{
        Path = $_
        Identity = Get-MsixIdentity -Path $_
    }
})
$expectedInputArchitectures = @('x64', 'arm64')
for ($index = 0; $index -lt $packageRecords.Count; $index++) {
    if ($packageRecords[$index].Identity.Architecture -ne $expectedInputArchitectures[$index]) {
        throw "The $($expectedInputArchitectures[$index]) input contains a '$($packageRecords[$index].Identity.Architecture)' package."
    }
}
$firstIdentity = $packageRecords[0].Identity
foreach ($record in $packageRecords) {
    if ($record.Identity.Name -ne 'PackagePilot.Desktop' -or
        $record.Identity.Publisher -ne 'CN=PackagePilot.Dev' -or
        $record.Identity.Version -ne $firstIdentity.Version -or
        $record.Identity.HasSignature) {
        throw "Input package '$($record.Path)' has an unexpected identity, version, publisher, or signature state."
    }
}

$architectures = @($packageRecords.Identity.Architecture | Sort-Object -Unique)
if (($architectures -join ',') -ne 'arm64,x64') {
    throw "The bundle requires exactly one x64 and one arm64 package; found '$($architectures -join ',')'."
}

$makeAppx = Resolve-MakeAppx
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$stagingDirectory = Join-Path $temporaryRoot "PackagePilot-Bundle-$([Guid]::NewGuid().ToString('N'))"
$resolvedStagingDirectory = [IO.Path]::GetFullPath($stagingDirectory)
if (-not $resolvedStagingDirectory.StartsWith(
        $temporaryRoot + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The bundle staging directory resolved outside the system temporary directory.'
}

try {
    [void][IO.Directory]::CreateDirectory($resolvedStagingDirectory)
    foreach ($record in $packageRecords) {
        $stagedName = "PackagePilot.$($record.Identity.Architecture).msix"
        Copy-Item -LiteralPath $record.Path -Destination (Join-Path $resolvedStagingDirectory $stagedName)
    }

    & $makeAppx 'bundle' '/v' '/o' '/bv' $firstIdentity.Version '/d' $resolvedStagingDirectory '/p' $resolvedOutput
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx bundle failed with exit code $LASTEXITCODE."
    }

    $bundleIdentity = Get-MsixBundleIdentity -Path $resolvedOutput
    if ($bundleIdentity.Name -ne $firstIdentity.Name -or
        $bundleIdentity.Publisher -ne $firstIdentity.Publisher -or
        $bundleIdentity.Version -ne $firstIdentity.Version -or
        ($bundleIdentity.Architectures -join ',') -ne 'arm64,x64' -or
        $bundleIdentity.HasSignature) {
        throw 'The generated bundle identity, architecture set, or signature state is invalid.'
    }
}
finally {
    if (Test-Path -LiteralPath $resolvedStagingDirectory) {
        Remove-Item -LiteralPath $resolvedStagingDirectory -Recurse -Force
    }
}

Get-Item -LiteralPath $resolvedOutput
