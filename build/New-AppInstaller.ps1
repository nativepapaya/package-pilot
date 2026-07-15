<#
.SYNOPSIS
Generates Package Pilot's public GitHub Releases App Installer feed.

.EXAMPLE
./build/New-AppInstaller.ps1 -Version '1.0.0.42' -OutputPath './artifacts/PackagePilot.appinstaller'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Version,

    [Parameter()]
    [string]$OutputPath = (Join-Path (Get-Location) 'PackagePilot.appinstaller'),

    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'nativepapaya/package-pilot',

    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$PackageAssetName = 'PackagePilot.msixbundle',

    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$AppInstallerAssetName = 'PackagePilot.appinstaller',

    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$X64RuntimeAssetName = 'Microsoft.WindowsAppRuntime.2.x64.msix',

    [Parameter()]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$Arm64RuntimeAssetName = 'Microsoft.WindowsAppRuntime.2.arm64.msix'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PackageVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $segments = $Value -split '\.'

    foreach ($segment in $segments) {
        [uint32]$numericValue = 0
        $isValidNumber = [uint32]::TryParse(
            $segment,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$numericValue)

        if (-not $isValidNumber -or $numericValue -gt [uint16]::MaxValue) {
            throw "Version '$Value' is not a valid MSIX version. Each of its four parts must be between 0 and 65535."
        }
    }
}

function New-ReleaseAssetUri {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryName,

        [Parameter(Mandatory)]
        [string]$AssetName
    )

    $escapedAssetName = [Uri]::EscapeDataString($AssetName)
    return "https://github.com/$RepositoryName/releases/latest/download/$escapedAssetName"
}

Assert-PackageVersion -Value $Version

$resolvedOutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([IO.Path]::GetExtension($resolvedOutputPath) -ine '.appinstaller') {
    throw "OutputPath must use the .appinstaller file extension: '$resolvedOutputPath'."
}

$outputDirectory = [IO.Path]::GetDirectoryName($resolvedOutputPath)
if (-not [IO.Directory]::Exists($outputDirectory)) {
    [void][IO.Directory]::CreateDirectory($outputDirectory)
}

$appInstallerUri = New-ReleaseAssetUri -RepositoryName $Repository -AssetName $AppInstallerAssetName
$packageUri = New-ReleaseAssetUri -RepositoryName $Repository -AssetName $PackageAssetName
$x64RuntimeUri = New-ReleaseAssetUri -RepositoryName $Repository -AssetName $X64RuntimeAssetName
$arm64RuntimeUri = New-ReleaseAssetUri -RepositoryName $Repository -AssetName $Arm64RuntimeAssetName

$schemaNamespace = 'http://schemas.microsoft.com/appx/appinstaller/2021'
$document = [Xml.XmlDocument]::new()
[void]$document.AppendChild($document.CreateXmlDeclaration('1.0', 'utf-8', $null))

$appInstaller = $document.CreateElement('AppInstaller', $schemaNamespace)
$appInstaller.SetAttribute('Version', $Version)
$appInstaller.SetAttribute('Uri', $appInstallerUri)
[void]$document.AppendChild($appInstaller)

$mainBundle = $document.CreateElement('MainBundle', $schemaNamespace)
$mainBundle.SetAttribute('Name', 'PackagePilot.Desktop')
$mainBundle.SetAttribute('Publisher', 'CN=PackagePilot.Dev')
$mainBundle.SetAttribute('Version', $Version)
$mainBundle.SetAttribute('Uri', $packageUri)
[void]$appInstaller.AppendChild($mainBundle)

$dependencies = $document.CreateElement('Dependencies', $schemaNamespace)
foreach ($runtime in @(
    @{ Architecture = 'x64'; Uri = $x64RuntimeUri }
    @{ Architecture = 'arm64'; Uri = $arm64RuntimeUri }
)) {
    $runtimePackage = $document.CreateElement('Package', $schemaNamespace)
    $runtimePackage.SetAttribute('Name', 'Microsoft.WindowsAppRuntime.2')
    $runtimePackage.SetAttribute(
        'Publisher',
        'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US')
    $runtimePackage.SetAttribute('Version', '2.2.0.0')
    $runtimePackage.SetAttribute('ProcessorArchitecture', $runtime.Architecture)
    $runtimePackage.SetAttribute('Uri', $runtime.Uri)
    [void]$dependencies.AppendChild($runtimePackage)
}
[void]$appInstaller.AppendChild($dependencies)

$updateSettings = $document.CreateElement('UpdateSettings', $schemaNamespace)
$onLaunch = $document.CreateElement('OnLaunch', $schemaNamespace)
$onLaunch.SetAttribute('HoursBetweenUpdateChecks', '24')
$onLaunch.SetAttribute('ShowPrompt', 'false')
$onLaunch.SetAttribute('UpdateBlocksActivation', 'false')
[void]$updateSettings.AppendChild($onLaunch)
[void]$updateSettings.AppendChild($document.CreateElement('AutomaticBackgroundTask', $schemaNamespace))
[void]$appInstaller.AppendChild($updateSettings)

$temporaryPath = "$resolvedOutputPath.$([Guid]::NewGuid().ToString('N')).tmp"
$writer = $null

try {
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.NewLineChars = [Environment]::NewLine
    $settings.NewLineHandling = [Xml.NewLineHandling]::Replace

    $writer = [Xml.XmlWriter]::Create($temporaryPath, $settings)
    $document.Save($writer)
    $writer.Close()
    $writer = $null

    Move-Item -LiteralPath $temporaryPath -Destination $resolvedOutputPath -Force
}
finally {
    if ($null -ne $writer) {
        $writer.Dispose()
    }

    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Get-Item -LiteralPath $resolvedOutputPath
