#Requires -Version 7.5

[CmdletBinding(DefaultParameterSetName = 'Path')]
param(
    [Parameter(Mandatory)]
    [ValidateSet('ReleaseMetadata', 'PreparedRelease')]
    [string]$DocumentKind,

    [Parameter(Mandatory, ParameterSetName = 'Path')]
    [ValidateNotNullOrEmpty()]
    [string]$LiteralPath,

    [Parameter(Mandatory, ParameterSetName = 'Bytes')]
    [ValidateNotNullOrEmpty()]
    [string]$Base64Utf8Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$schemaFileName = switch ($DocumentKind) {
    'ReleaseMetadata' { 'release-metadata.schema.json' }
    'PreparedRelease' { 'prepared-release.schema.json' }
    default { throw "Unsupported release JSON document kind '$DocumentKind'." }
}

$schemaPath = Join-Path $PSScriptRoot "schemas\$schemaFileName"
if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) {
    throw "The committed schema for '$DocumentKind' was not found."
}
$resolvedSchemaPath = (Resolve-Path -LiteralPath $schemaPath).Path
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)

if ($PSCmdlet.ParameterSetName -eq 'Path') {
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "The '$DocumentKind' JSON document was not found."
    }
    $resolvedDocumentPath = (Resolve-Path -LiteralPath $LiteralPath).Path
    try {
        $json = $strictUtf8.GetString([IO.File]::ReadAllBytes($resolvedDocumentPath))
    }
    catch {
        throw "The '$DocumentKind' JSON document is not valid UTF-8: $($_.Exception.Message)"
    }
}
else {
    try {
        $documentBytes = [Convert]::FromBase64String($Base64Utf8Json)
        $json = $strictUtf8.GetString($documentBytes)
    }
    catch {
        throw "The '$DocumentKind' JSON byte payload is not valid Base64 UTF-8: $($_.Exception.Message)"
    }
}

try {
    $isValid = Test-Json `
        -Json $json `
        -SchemaFile $resolvedSchemaPath `
        -ErrorAction Stop
}
catch {
    throw "The '$DocumentKind' JSON document failed strict schema validation: $($_.Exception.Message)"
}

if (-not $isValid) {
    throw "The '$DocumentKind' JSON document failed strict schema validation."
}

try {
    $document = $json | ConvertFrom-Json -AsHashtable -DateKind String
}
catch {
    throw "The '$DocumentKind' JSON document failed deterministic parsing: $($_.Exception.Message)"
}

$timestampProperty = if ($DocumentKind -eq 'ReleaseMetadata') {
    'generatedAtUtc'
}
else {
    'preparedAtUtc'
}
$timestamp = [DateTimeOffset]::MinValue
$timestampText = [string]$document[$timestampProperty]
if (-not [DateTimeOffset]::TryParseExact(
        $timestampText,
        'yyyy-MM-ddTHH:mm:ss.fffffffzzz',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$timestamp) -or
    $timestamp.Offset -ne [TimeSpan]::Zero) {
    throw "The '$DocumentKind' JSON document failed strict schema validation: invalid UTC timestamp."
}

Write-Output $true
