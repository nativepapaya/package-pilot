[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\src\PackagePilot.App\Package.appxmanifest')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "MSIX versions must contain exactly four numeric segments: '$Version'."
}

$segments = $Version.Split('.')
foreach ($segment in $segments) {
    $value = 0
    if (-not [int]::TryParse($segment, [ref]$value) -or $value -lt 0 -or $value -gt [uint16]::MaxValue) {
        throw "Each MSIX version segment must be an integer from 0 through 65535: '$Version'."
    }
}

$resolvedManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$document = [System.Xml.XmlDocument]::new()
$document.PreserveWhitespace = $true
$document.Load($resolvedManifest)

$namespaces = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
$namespaces.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$identity = $document.SelectSingleNode('/appx:Package/appx:Identity', $namespaces)

if ($null -eq $identity) {
    throw "The package Identity element was not found in '$resolvedManifest'."
}

$identity.SetAttribute('Version', $Version)

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Encoding = [System.Text.UTF8Encoding]::new($false)
$settings.Indent = $false
$settings.NewLineChars = "`r`n"
$settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace

$writer = [System.Xml.XmlWriter]::Create($resolvedManifest, $settings)
try {
    $document.Save($writer)
}
finally {
    $writer.Dispose()
}

Write-Output "Set Package.appxmanifest version to $Version."
