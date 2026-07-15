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

function Assert-RejectedVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$ManifestPath
    )

    $wasRejected = $false
    try {
        & $script:SetVersionScript -Version $Version -ManifestPath $ManifestPath | Out-Null
    }
    catch {
        $wasRejected = $true
    }

    Assert-True -Condition $wasRejected -Message "Version '$Version' should have been rejected."
}

$SetVersionScript = Join-Path $PSScriptRoot '..\Set-PackageVersion.ps1'
$sourceManifest = Join-Path $PSScriptRoot '..\..\src\PackagePilot.App\Package.appxmanifest'
$sourceHashBefore = (Get-FileHash -LiteralPath $sourceManifest -Algorithm SHA256).Hash
$temporaryManifest = Join-Path ([IO.Path]::GetTempPath()) "PackagePilot-$([Guid]::NewGuid().ToString('N')).appxmanifest"

try {
    Copy-Item -LiteralPath $sourceManifest -Destination $temporaryManifest

    & $SetVersionScript -Version '1.0.123.0' -ManifestPath $temporaryManifest | Out-Null

    [xml]$updatedManifest = Get-Content -LiteralPath $temporaryManifest -Raw
    $namespaces = [Xml.XmlNamespaceManager]::new($updatedManifest.NameTable)
    $namespaces.AddNamespace('appx', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $updatedManifest.SelectSingleNode('/appx:Package/appx:Identity', $namespaces)
    Assert-True -Condition ($null -ne $identity) -Message 'The test manifest Identity element is missing.'
    Assert-True -Condition ($identity.GetAttribute('Version') -eq '1.0.123.0') `
        -Message 'The package version was not replaced.'

    Assert-RejectedVersion -Version '1.0.invalid.0' -ManifestPath $temporaryManifest
    Assert-RejectedVersion -Version '1.0.65536.0' -ManifestPath $temporaryManifest
    Assert-RejectedVersion -Version '1.0.+2.0' -ManifestPath $temporaryManifest
    Assert-RejectedVersion -Version '1.0. 2.0' -ManifestPath $temporaryManifest

    $sourceHashAfter = (Get-FileHash -LiteralPath $sourceManifest -Algorithm SHA256).Hash
    Assert-True -Condition ($sourceHashAfter -eq $sourceHashBefore) `
        -Message 'The source manifest changed while testing a temporary copy.'
}
finally {
    if (Test-Path -LiteralPath $temporaryManifest) {
        Remove-Item -LiteralPath $temporaryManifest -Force
    }
}

Write-Output 'Set-PackageVersion tests passed.'
