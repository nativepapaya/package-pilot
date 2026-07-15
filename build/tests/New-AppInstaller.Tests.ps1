[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [object]$Actual,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [object]$Expected,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', but found '$Actual'."
    }
}

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

$generatorPath = Join-Path $PSScriptRoot '..\New-AppInstaller.ps1'
$testVersion = '2.3.4.5'
$outputPath = Join-Path ([IO.Path]::GetTempPath()) "PackagePilot-$([Guid]::NewGuid().ToString('N')).appinstaller"
$invalidOutputPath = Join-Path ([IO.Path]::GetTempPath()) "PackagePilot-invalid-$([Guid]::NewGuid().ToString('N')).appinstaller"

try {
    & $generatorPath -Version $testVersion -OutputPath $outputPath | Out-Null

    $bytes = [IO.File]::ReadAllBytes($outputPath)
    $hasUtf8Bom = $bytes.Length -ge 3 `
        -and $bytes[0] -eq 0xEF `
        -and $bytes[1] -eq 0xBB `
        -and $bytes[2] -eq 0xBF
    Assert-True -Condition (-not $hasUtf8Bom) -Message 'The App Installer file must be UTF-8 without a byte-order mark.'

    $content = [IO.File]::ReadAllText($outputPath, [Text.Encoding]::UTF8)
    Assert-True -Condition ($content.StartsWith('<?xml version="1.0" encoding="utf-8"?>')) `
        -Message 'The App Installer file must declare UTF-8 XML encoding.'
    Assert-True -Condition (-not ($content.ToCharArray() | Where-Object { [int]$_ -gt 127 })) `
        -Message 'The App Installer file must contain only ASCII characters.'

    $document = [Xml.XmlDocument]::new()
    $document.LoadXml($content)

    $namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('ai', 'http://schemas.microsoft.com/appx/appinstaller/2021')

    $appInstaller = $document.SelectSingleNode('/ai:AppInstaller', $namespaces)
    Assert-True -Condition ($null -ne $appInstaller) -Message 'The 2021 App Installer root element is missing.'
    Assert-Equal -Actual $appInstaller.GetAttribute('Version') -Expected $testVersion -Message 'App Installer version mismatch.'
    Assert-Equal -Actual $appInstaller.GetAttribute('Uri') `
        -Expected 'https://github.com/nativepapaya/package-pilot/releases/latest/download/PackagePilot.appinstaller' `
        -Message 'App Installer release URI mismatch.'

    $mainPackage = $document.SelectSingleNode('/ai:AppInstaller/ai:MainPackage', $namespaces)
    Assert-True -Condition ($null -ne $mainPackage) -Message 'MainPackage is missing.'
    Assert-Equal -Actual $mainPackage.GetAttribute('Name') -Expected 'PackagePilot.Desktop' -Message 'Main package name mismatch.'
    Assert-Equal -Actual $mainPackage.GetAttribute('Publisher') -Expected 'CN=PackagePilot.Dev' -Message 'Main package publisher mismatch.'
    Assert-Equal -Actual $mainPackage.GetAttribute('Version') -Expected $testVersion -Message 'Main package version mismatch.'
    Assert-Equal -Actual $mainPackage.GetAttribute('ProcessorArchitecture') -Expected 'x64' -Message 'Main package architecture mismatch.'
    Assert-Equal -Actual $mainPackage.GetAttribute('Uri') `
        -Expected 'https://github.com/nativepapaya/package-pilot/releases/latest/download/PackagePilot.msix' `
        -Message 'Main package release URI mismatch.'

    $runtimePackage = $document.SelectSingleNode('/ai:AppInstaller/ai:Dependencies/ai:Package', $namespaces)
    Assert-True -Condition ($null -ne $runtimePackage) -Message 'Windows App Runtime dependency is missing.'
    Assert-Equal -Actual $runtimePackage.GetAttribute('Name') -Expected 'Microsoft.WindowsAppRuntime.2' -Message 'Runtime package name mismatch.'
    Assert-Equal -Actual $runtimePackage.GetAttribute('Publisher') `
        -Expected 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' `
        -Message 'Runtime package publisher mismatch.'
    Assert-Equal -Actual $runtimePackage.GetAttribute('Version') -Expected '2.2.0.0' -Message 'Runtime package version mismatch.'
    Assert-Equal -Actual $runtimePackage.GetAttribute('ProcessorArchitecture') -Expected 'x64' -Message 'Runtime package architecture mismatch.'
    Assert-Equal -Actual $runtimePackage.GetAttribute('Uri') `
        -Expected 'https://github.com/nativepapaya/package-pilot/releases/latest/download/Microsoft.WindowsAppRuntime.2.msix' `
        -Message 'Runtime package release URI mismatch.'

    $onLaunch = $document.SelectSingleNode('/ai:AppInstaller/ai:UpdateSettings/ai:OnLaunch', $namespaces)
    Assert-True -Condition ($null -ne $onLaunch) -Message 'OnLaunch update settings are missing.'
    Assert-Equal -Actual $onLaunch.GetAttribute('HoursBetweenUpdateChecks') -Expected '0' -Message 'Update check interval mismatch.'
    Assert-Equal -Actual $onLaunch.GetAttribute('ShowPrompt') -Expected 'true' -Message 'Update prompt setting mismatch.'
    Assert-Equal -Actual $onLaunch.GetAttribute('UpdateBlocksActivation') -Expected 'false' -Message 'Launch-blocking setting mismatch.'

    $backgroundTask = $document.SelectSingleNode('/ai:AppInstaller/ai:UpdateSettings/ai:AutomaticBackgroundTask', $namespaces)
    Assert-True -Condition ($null -ne $backgroundTask) -Message 'Automatic background update task is missing.'

    $childOrder = @($appInstaller.ChildNodes | Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element } | ForEach-Object LocalName)
    Assert-Equal -Actual ($childOrder -join ',') -Expected 'MainPackage,Dependencies,UpdateSettings' `
        -Message 'App Installer child element order mismatch.'

    $invalidVersionWasRejected = $false
    try {
        & $generatorPath -Version '65536.0.0.0' -OutputPath $invalidOutputPath | Out-Null
    }
    catch {
        $invalidVersionWasRejected = $true
    }

    Assert-True -Condition $invalidVersionWasRejected -Message 'An out-of-range MSIX version was not rejected.'
}
finally {
    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Force
    }

    if (Test-Path -LiteralPath $invalidOutputPath) {
        Remove-Item -LiteralPath $invalidOutputPath -Force
    }
}

Write-Output 'New-AppInstaller tests passed.'
