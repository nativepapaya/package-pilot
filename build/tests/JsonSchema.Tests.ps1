#Requires -Version 7.5

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

function Copy-JsonObject {
    param(
        [Parameter(Mandatory)]
        [object]$InputObject
    )

    $copy = $InputObject |
        ConvertTo-Json -Depth 12 |
        ConvertFrom-Json -AsHashtable -DateKind String
    foreach ($timestampName in @('generatedAtUtc', 'preparedAtUtc')) {
        if ($copy.Contains($timestampName) -and
            $InputObject.Contains($timestampName)) {
            $copy[$timestampName] = [string]$InputObject[$timestampName]
        }
    }

    return $copy
}

function ConvertTo-TestJson {
    param(
        [Parameter(Mandatory)]
        [object]$InputObject
    )

    return $InputObject | ConvertTo-Json -Depth 12
}

function Assert-SchemaAccepts {
    param(
        [Parameter(Mandatory)]
        [string]$DocumentKind,

        [Parameter(Mandatory)]
        [string]$Json,

        [Parameter(Mandatory)]
        [string]$CaseName
    )

    [IO.File]::WriteAllText(
        $script:FixturePath,
        $Json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    $result = & $script:ValidatorPath `
        -DocumentKind $DocumentKind `
        -LiteralPath $script:FixturePath
    Assert-True `
        -Condition ($result -eq $true) `
        -Message "Schema rejected valid fixture '$CaseName'."
}

function Assert-SchemaRejects {
    param(
        [Parameter(Mandatory)]
        [string]$DocumentKind,

        [Parameter(Mandatory)]
        [string]$Json,

        [Parameter(Mandatory)]
        [string]$CaseName
    )

    [IO.File]::WriteAllText(
        $script:FixturePath,
        $Json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    $failure = $null
    try {
        & $script:ValidatorPath `
            -DocumentKind $DocumentKind `
            -LiteralPath $script:FixturePath |
            Out-Null
    }
    catch {
        $failure = $_.Exception.Message
    }

    Assert-True `
        -Condition (-not [string]::IsNullOrWhiteSpace($failure)) `
        -Message "Schema accepted invalid fixture '$CaseName'."
    Assert-True `
        -Condition ($failure -like '*failed strict schema validation*') `
        -Message "Fixture '$CaseName' failed outside the expected validation boundary: '$failure'"
}

$buildRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$script:ValidatorPath = Join-Path $buildRoot 'Test-JsonSchema.ps1'
$releaseSchemaPath = Join-Path $buildRoot 'schemas\release-metadata.schema.json'
$preparedSchemaPath = Join-Path $buildRoot 'schemas\prepared-release.schema.json'

foreach ($requiredPath in @(
    $script:ValidatorPath,
    $releaseSchemaPath,
    $preparedSchemaPath
)) {
    Assert-True `
        -Condition (Test-Path -LiteralPath $requiredPath -PathType Leaf) `
        -Message "Required schema test input '$requiredPath' is missing."
}

$validator = Get-Content -LiteralPath $script:ValidatorPath -Raw
Assert-True `
    -Condition (
        $validator -match "ValidateSet\('ReleaseMetadata', 'PreparedRelease'\)" -and
        $validator -match 'Test-Json' -and
        $validator -match '-SchemaFile\s+\$resolvedSchemaPath') `
    -Message 'The validator must use a closed document-kind allowlist and its committed schema.'

foreach ($schemaPath in @($releaseSchemaPath, $preparedSchemaPath)) {
    $schema = Get-Content -LiteralPath $schemaPath -Raw |
        ConvertFrom-Json -AsHashtable
    Assert-True `
        -Condition (
            $schema['$schema'] -eq 'https://json-schema.org/draft/2020-12/schema' -and
            $schema.type -eq 'object' -and
            $schema.additionalProperties -eq $false) `
        -Message "Schema '$schemaPath' is not a closed JSON Schema 2020-12 object."
}

$releaseMetadata = [ordered]@{
    schemaVersion = 2
    packageVersion = '1.0.0.20'
    architectures = @('x64', 'arm64')
    packageAsset = 'PackagePilot.msixbundle'
    releaseSequence = [uint64]20
    tagName = 'v1.0.20'
    repository = 'nativepapaya/package-pilot'
    commitSha = '0123456789abcdef0123456789abcdef01234567'
    runId = [uint64]123456789
    runNumber = [uint64]16
    runAttempt = [uint64]1
    artifactName = 'package-pilot-unsigned-123456789-1'
    workflowName = 'Release'
    generatedAtUtc = '2026-07-19T12:34:56.1234567+00:00'
}
$releaseJson = ConvertTo-TestJson -InputObject $releaseMetadata

$fileNames = @(
    'PackagePilot.msixbundle'
    'Microsoft.WindowsAppRuntime.2.x64.msix'
    'Microsoft.WindowsAppRuntime.2.arm64.msix'
    'PackagePilot.cer'
    'PackagePilot.appinstaller'
    'SHA256SUMS.txt'
)
$preparedRelease = [ordered]@{
    schemaVersion = 1
    repository = 'nativepapaya/package-pilot'
    workflowName = 'Release'
    workflowDatabaseId = [uint64]77
    runId = [uint64]123456789
    runNumber = [uint64]16
    runAttempt = [uint64]1
    artifactId = [uint64]987654321
    artifactName = 'package-pilot-unsigned-123456789-1'
    commitSha = '0123456789abcdef0123456789abcdef01234567'
    releaseSequence = [uint64]20
    tagName = 'v1.0.20'
    packageVersion = '1.0.0.20'
    signerThumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'
    preparedAtUtc = '2026-07-19T12:35:56.1234567+00:00'
    sourceMetadata = [ordered]@{
        name = 'release-metadata.json'
        size = [uint64]512
        sha256 = ('a' * 64)
    }
    assets = @($fileNames | ForEach-Object {
        [ordered]@{
            name = $_
            size = [uint64]1024
            sha256 = ('b' * 64)
        }
    })
}
$preparedJson = ConvertTo-TestJson -InputObject $preparedRelease

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$temporaryDirectory = [IO.Path]::GetFullPath((Join-Path `
    $temporaryRoot `
    "PackagePilot-json-schema-test-$([Guid]::NewGuid().ToString('N'))"))
if (-not $temporaryDirectory.StartsWith(
        $temporaryRoot + '\',
        [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($temporaryDirectory) -notmatch
        '^PackagePilot-json-schema-test-[0-9a-f]{32}$') {
    throw 'The JSON schema test directory resolved outside the temporary directory.'
}

[void][IO.Directory]::CreateDirectory($temporaryDirectory)
$script:FixturePath = Join-Path $temporaryDirectory 'fixture.json'
try {
    Assert-SchemaAccepts `
        -DocumentKind ReleaseMetadata `
        -Json $releaseJson `
        -CaseName 'valid release metadata'
    Assert-SchemaAccepts `
        -DocumentKind PreparedRelease `
        -Json $preparedJson `
        -CaseName 'valid prepared release'

    $base64Result = & $script:ValidatorPath `
        -DocumentKind PreparedRelease `
        -Base64Utf8Json ([Convert]::ToBase64String(
            [Text.UTF8Encoding]::new($false).GetBytes($preparedJson)))
    Assert-True `
        -Condition ($base64Result -eq $true) `
        -Message 'Schema rejected a valid exact-byte prepared release payload.'

    $invalid = Copy-JsonObject -InputObject $releaseMetadata
    $invalid.unexpected = $true
    Assert-SchemaRejects ReleaseMetadata (ConvertTo-TestJson $invalid) 'release unknown property'

    $invalid = Copy-JsonObject -InputObject $releaseMetadata
    [void]$invalid.Remove('generatedAtUtc')
    Assert-SchemaRejects ReleaseMetadata (ConvertTo-TestJson $invalid) 'release missing property'

    $invalid = Copy-JsonObject -InputObject $releaseMetadata
    $invalid.runId = '123456789'
    Assert-SchemaRejects ReleaseMetadata (ConvertTo-TestJson $invalid) 'release numeric string'

    $invalid = Copy-JsonObject -InputObject $releaseMetadata
    $invalid.architectures = @('arm64', 'x64')
    Assert-SchemaRejects ReleaseMetadata (ConvertTo-TestJson $invalid) 'release architecture order'

    $invalid = Copy-JsonObject -InputObject $releaseMetadata
    $invalid.generatedAtUtc = '2026-07-19T12:34:56.1234567Z'
    Assert-SchemaRejects ReleaseMetadata (ConvertTo-TestJson $invalid) 'release timestamp shape'

    $invalid = Copy-JsonObject -InputObject $releaseMetadata
    $invalid.generatedAtUtc = '2026-02-31T12:34:56.1234567+00:00'
    Assert-SchemaRejects ReleaseMetadata (ConvertTo-TestJson $invalid) 'release impossible calendar date'

    $duplicateReleaseJson = $releaseJson.Replace(
        '"schemaVersion": 2,',
        '"schemaVersion": 2, "schemaVersion": 2,')
    Assert-True `
        -Condition ($duplicateReleaseJson -ne $releaseJson) `
        -Message 'The duplicate-key release fixture was not constructed.'
    Assert-SchemaRejects ReleaseMetadata $duplicateReleaseJson 'release duplicate key'

    $invalid = Copy-JsonObject -InputObject $preparedRelease
    $invalid.unexpected = $true
    Assert-SchemaRejects PreparedRelease (ConvertTo-TestJson $invalid) 'prepared unknown property'

    $invalid = Copy-JsonObject -InputObject $preparedRelease
    [void]$invalid.Remove('signerThumbprint')
    Assert-SchemaRejects PreparedRelease (ConvertTo-TestJson $invalid) 'prepared missing property'

    $invalid = Copy-JsonObject -InputObject $preparedRelease
    $first = $invalid.assets[0]
    $invalid.assets[0] = $invalid.assets[1]
    $invalid.assets[1] = $first
    Assert-SchemaRejects PreparedRelease (ConvertTo-TestJson $invalid) 'prepared asset order'

    $invalid = Copy-JsonObject -InputObject $preparedRelease
    $invalid.preparedAtUtc = '2026-07-19T12:35:56Z'
    Assert-SchemaRejects PreparedRelease (ConvertTo-TestJson $invalid) 'prepared timestamp shape'

    $invalid = Copy-JsonObject -InputObject $preparedRelease
    $invalid.preparedAtUtc = '2026-02-31T12:35:56.1234567+00:00'
    Assert-SchemaRejects PreparedRelease (ConvertTo-TestJson $invalid) 'prepared impossible calendar date'

    $invalid = Copy-JsonObject -InputObject $preparedRelease
    $invalid.sourceMetadata.sha256 = ('A' * 64)
    Assert-SchemaRejects PreparedRelease (ConvertTo-TestJson $invalid) 'prepared hash case'

    $invalid = Copy-JsonObject -InputObject $preparedRelease
    $invalid.assets[0].size = '1024'
    Assert-SchemaRejects PreparedRelease (ConvertTo-TestJson $invalid) 'prepared size type'
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory -PathType Container) {
        $cleanupPath = [IO.Path]::GetFullPath($temporaryDirectory)
        if (-not $cleanupPath.StartsWith(
                $temporaryRoot + '\',
                [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($cleanupPath) -notmatch
                '^PackagePilot-json-schema-test-[0-9a-f]{32}$') {
            throw "Refusing to clean unsafe JSON schema test path '$cleanupPath'."
        }
        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
    }
}

Write-Output 'Release JSON schema tests passed.'
