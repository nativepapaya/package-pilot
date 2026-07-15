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
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        (Resolve-Path -LiteralPath $Path).Path,
        [ref]$tokens,
        [ref]$errors)

    if ($errors.Count -gt 0) {
        $messages = $errors | ForEach-Object Message
        throw "'$Path' has PowerShell parse errors: $($messages -join '; ')"
    }

    return $ast
}

$buildRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $buildRoot '..')).Path
$initializerPath = Join-Path $buildRoot 'Initialize-ManualReleaseCertificate.ps1'
$publisherPath = Join-Path $buildRoot 'Publish-ManualRelease.ps1'
$workflowPath = Join-Path $repositoryRoot '.github\workflows\release.yml'

$initializerAst = Assert-PowerShellParses -Path $initializerPath
$publisherAst = Assert-PowerShellParses -Path $publisherPath
$initializer = Get-Content -LiteralPath $initializerPath -Raw
$publisher = Get-Content -LiteralPath $publisherPath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw

$forbiddenPfxCommands = @($initializerAst, $publisherAst).ForEach({
    $_.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Export-PfxCertificate'
        },
        $true)
})

Assert-True -Condition ($forbiddenPfxCommands.Count -eq 0) `
    -Message 'Manual release scripts must never export the private key to a PFX.'
Assert-True -Condition ($initializer -match '(?m)^\s*KeyExportPolicy\s*=\s*''NonExportable''\s*$') `
    -Message 'The manual release certificate must be created with KeyExportPolicy NonExportable.'
Assert-True -Condition ($initializer -match [regex]::Escape("Cert:\LocalMachine\TrustedPeople")) `
    -Message 'The certificate initializer must trust the public certificate in LocalMachine\TrustedPeople.'
Assert-True -Condition ($publisher -match [regex]::Escape('AppxSignature.p7x')) `
    -Message 'The manual publisher must validate the MSIX signature container.'
Assert-True -Condition ($publisher -match 'Get-AuthenticodeSignature') `
    -Message 'The manual publisher must verify Authenticode signatures.'
Assert-True -Condition ($publisher -match 'SHA256SUMS\.txt') `
    -Message 'The manual publisher must create release checksums.'
Assert-True -Condition ($publisher -match '[''"]release[''"]\s*,\s*[''"]create[''"]') `
    -Message 'The manual publisher must create the GitHub release through gh.'
Assert-True -Condition ($publisher -match '(?m)^\$Repository\s*=\s*''nativepapaya/package-pilot''\s*$') `
    -Message 'The release signing key must be restricted to the official Package Pilot repository.'
Assert-True -Condition ($publisher -match 'Get-ReleaseHighWaterMark') `
    -Message 'The manual publisher must enforce a monotonically increasing release high-water mark.'
Assert-True -Condition ($publisher -match 'workflowDatabaseId') `
    -Message 'The manual publisher must bind selected runs to the official Release workflow ID.'
Assert-True -Condition ($publisher -match 'merge_base_commit') `
    -Message 'The manual publisher must verify that the selected commit remains on main.'
Assert-True -Condition ($publisher -match 'Get-ExactTagReference') `
    -Message 'The manual publisher must refuse to reuse an existing Git tag.'

Assert-True -Condition ($workflow -notmatch '(?i)PACKAGEPILOT_SIGNING_PFX|secrets\.|sign_release|contents:\s*write') `
    -Message 'The hosted Release workflow must not request signing secrets or write access.'
Assert-True -Condition ($workflow -match 'release-metadata\.json') `
    -Message 'The hosted Release workflow must include release-metadata.json.'
Assert-True -Condition ($workflow -match '(?m)^\s*retention-days:\s*30\s*$') `
    -Message 'Unsigned release artifacts must be retained for 30 days.'

Write-Output 'Manual release security tests passed.'
