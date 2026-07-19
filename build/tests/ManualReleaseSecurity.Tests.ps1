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

function Add-TestZipEntry {
    param(
        [Parameter(Mandatory)]
        [object]$Archive,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [byte[]]$Content
    )

    $entry = $Archive.CreateEntry(
        $Name,
        [IO.Compression.CompressionLevel]::NoCompression)
    $stream = $entry.Open()
    try {
        $stream.Write($Content, 0, $Content.Length)
    }
    finally {
        $stream.Dispose()
    }
}

function New-TestApplicationMsixBytes {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('x64', 'arm64')]
        [string]$Architecture,

        [switch]$OmitReadOnlyPayload,

        [switch]$Signed,

        [switch]$IncludePowerShellPayload
    )

    $memory = [IO.MemoryStream]::new()
    $archive = [IO.Compression.ZipArchive]::new(
        $memory,
        [IO.Compression.ZipArchiveMode]::Create,
        $true)
    try {
        $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="PackagePilot.Desktop" Publisher="CN=PackagePilot.Dev" Version="9.8.7.6" ProcessorArchitecture="$Architecture" />
</Package>
"@
        Add-TestZipEntry `
            -Archive $archive `
            -Name 'AppxManifest.xml' `
            -Content ([Text.UTF8Encoding]::new($false).GetBytes($manifest))

        foreach ($payloadName in $RequiredApplicationPayloadNames) {
            if ($OmitReadOnlyPayload -and
                $payloadName -eq 'PackagePilot.Windows.ReadOnly.dll') {
                continue
            }

            Add-TestZipEntry `
                -Archive $archive `
                -Name $payloadName `
                -Content ([Text.UTF8Encoding]::new($false).GetBytes("test:$payloadName"))
        }
        if ($IncludePowerShellPayload) {
            Add-TestZipEntry `
                -Archive $archive `
                -Name 'tools/pwsh.exe' `
                -Content ([byte[]](1, 2, 3, 4))
        }
        if ($Signed) {
            Add-TestZipEntry `
                -Archive $archive `
                -Name 'AppxSignature.p7x' `
                -Content ([byte[]](1, 2, 3, 4))
        }
    }
    finally {
        $archive.Dispose()
    }

    try {
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
    }
}

function New-TestMsixBundle {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [ValidateRange(2, 3)]
        [int]$PackageCount = 2,

        [switch]$OmitArm64ReadOnlyPayload,

        [ValidateSet('x64', 'arm64')]
        [string]$Arm64InnerArchitecture = 'arm64',

        [switch]$Signed,

        [switch]$OmitArm64Signature,

        [switch]$IncludePowerShellPayload
    )

    $packageRecords = @(
        [pscustomobject]@{
            FileName = 'PackagePilot.x64.msix'
            ManifestArchitecture = 'x64'
            InnerArchitecture = 'x64'
            OmitReadOnlyPayload = $false
        }
        [pscustomobject]@{
            FileName = 'PackagePilot.arm64.msix'
            ManifestArchitecture = 'arm64'
            InnerArchitecture = $Arm64InnerArchitecture
            OmitReadOnlyPayload = [bool]$OmitArm64ReadOnlyPayload
        }
    )
    if ($PackageCount -eq 3) {
        $packageRecords += [pscustomobject]@{
            FileName = 'PackagePilot.extra.msix'
            ManifestArchitecture = 'x64'
            InnerArchitecture = 'x64'
            OmitReadOnlyPayload = $false
        }
    }

    $packageXml = @($packageRecords | ForEach-Object {
        "    <Package Type=`"application`" FileName=`"$($_.FileName)`" Architecture=`"$($_.ManifestArchitecture)`" Version=`"9.8.7.6`" />"
    }) -join [Environment]::NewLine
    $bundleManifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle">
  <Identity Name="PackagePilot.Desktop" Publisher="CN=PackagePilot.Dev" Version="9.8.7.6" />
  <Packages>
$packageXml
  </Packages>
</Bundle>
"@

    $fileStream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new(
        $fileStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $false)
    try {
        Add-TestZipEntry `
            -Archive $archive `
            -Name 'AppxMetadata/AppxBundleManifest.xml' `
            -Content ([Text.UTF8Encoding]::new($false).GetBytes($bundleManifest))

        foreach ($packageRecord in $packageRecords) {
            $packageSigned = $Signed -and
                (-not $OmitArm64Signature -or
                    $packageRecord.ManifestArchitecture -ne 'arm64')
            $packageBytes = New-TestApplicationMsixBytes `
                -Architecture $packageRecord.InnerArchitecture `
                -OmitReadOnlyPayload:$packageRecord.OmitReadOnlyPayload `
                -Signed:$packageSigned `
                -IncludePowerShellPayload:$IncludePowerShellPayload
            Add-TestZipEntry `
                -Archive $archive `
                -Name $packageRecord.FileName `
                -Content $packageBytes
        }
        if ($Signed) {
            Add-TestZipEntry `
                -Archive $archive `
                -Name 'AppxSignature.p7x' `
                -Content ([byte[]](1, 2, 3, 4))
        }
    }
    finally {
        $archive.Dispose()
    }
}

$buildRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $buildRoot '..')).Path
$initializerPath = Join-Path $buildRoot 'Initialize-ManualReleaseCertificate.ps1'
$publisherPath = Join-Path $buildRoot 'Publish-ManualRelease.ps1'
$workflowPath = Join-Path $repositoryRoot '.github\workflows\release.yml'
$parallelBuildPath = Join-Path $buildRoot 'Build-UnsignedPackages.ps1'
$stageUnsignedReleasePath = Join-Path $buildRoot 'Stage-UnsignedRelease.ps1'

$initializerAst = Assert-PowerShellParses -Path $initializerPath
$publisherAst = Assert-PowerShellParses -Path $publisherPath
$initializer = Get-Content -LiteralPath $initializerPath -Raw
$publisher = Get-Content -LiteralPath $publisherPath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$releaseTooling = @(
    $workflow
    (Get-Content -LiteralPath $parallelBuildPath -Raw)
    (Get-Content -LiteralPath $stageUnsignedReleasePath -Raw)
) -join [Environment]::NewLine

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
    -Message 'The manual publisher must validate the MSIX bundle signature container.'
Assert-True -Condition ($publisher -match 'Get-MsixBundleIdentity') `
    -Message 'The manual publisher must validate the bundle identity and architecture set.'
Assert-True -Condition (
    $publisher -match [regex]::Escape('$packages.Count -ne 2') -and
    $publisher -match [regex]::Escape('EmbeddedPackages') -and
    $publisher -match [regex]::Escape('PackagePilot.Windows.ReadOnly.dll') -and
    $publisher -match [regex]::Escape('RequireApplicationPayload')) `
    -Message 'The manual publisher must inspect exactly two embedded application MSIX identities and their required payloads.'
Assert-True -Condition ($publisher -match [regex]::Escape('PackagePilot.msixbundle')) `
    -Message 'The manual publisher must sign and release the multi-architecture bundle.'
Assert-True -Condition (
    $publisher -match [regex]::Escape('Microsoft.WindowsAppRuntime.2.x64.msix') -and
    $publisher -match [regex]::Escape('Microsoft.WindowsAppRuntime.2.arm64.msix')) `
    -Message 'The manual publisher must validate both architecture-specific runtime dependencies.'
Assert-True -Condition ($publisher -match 'Get-AuthenticodeSignature') `
    -Message 'The manual publisher must verify Authenticode signatures.'
Assert-True -Condition ($publisher -match 'SHA256SUMS\.txt') `
    -Message 'The manual publisher must create release checksums.'
Assert-True -Condition ($publisher -match '[''"]release[''"]\s*[''"]create[''"]') `
    -Message 'The manual publisher must create the GitHub draft through gh.'
Assert-True -Condition (
    $publisher -match "ParameterSetName\s*=\s*'Prepare'" -and
    $publisher -match "ParameterSetName\s*=\s*'Promote'" -and
    $publisher -match '\[switch\]\$Prepare' -and
    $publisher -match '\[switch\]\$Promote' -and
    $publisher -match '\[string\]\$ConfirmTag' -and
    $publisher -match '\[string\]\$ConfirmBundleSha256') `
    -Message 'The manual publisher must expose mutually exclusive Prepare and Promote modes.'
Assert-True -Condition (
    $publisher -match '(?s)\[Parameter\(Mandatory,\s*ParameterSetName\s*=\s*''Prepare''\)\]\s*\[Parameter\(Mandatory,\s*ParameterSetName\s*=\s*''Promote''\)\]\s*\[uint64\]\$RunId' -and
    $publisher -match '(?s)\[Parameter\(Mandatory,\s*ParameterSetName\s*=\s*''Promote''\)\].*?\[string\]\$ConfirmTag') `
    -Message 'Both phases must require the exact run ID, and promotion must require exact tag and bundle confirmations.'
Assert-True -Condition (
    $publisher -match [regex]::Escape('prepared-release.json.sig') -and
    $publisher -match [regex]::Escape('SignData(') -and
    $publisher -match [regex]::Escape('VerifyData(')) `
    -Message 'The durable prepared state must be signed and verified with the release certificate.'
Assert-True -Condition (
    $publisher -match [regex]::Escape(". (Join-Path `$PSScriptRoot 'NativeCommand.ps1')") -and
    $publisher -match [regex]::Escape('Invoke-NativeChecked')) `
    -Message 'The manual publisher must route native tools through the checked command boundary.'
Assert-True -Condition (
    $publisher -match [regex]::Escape("'--draft'") -and
    $publisher -match [regex]::Escape("'--latest=false'") -and
    $publisher -match [regex]::Escape("'--draft=false'") -and
    $publisher -match '[''"]release[''"]\s*[''"]upload[''"]' -and
    $publisher -notmatch [regex]::Escape('--clobber')) `
    -Message 'Promotion must stage a non-latest draft, resume only missing assets, and publish explicitly without clobbering.'
Assert-True -Condition (
    $publisher.IndexOf("Mode = 'Prepared'", [StringComparison]::Ordinal) -lt
    $publisher.IndexOf("'--latest=false'", [StringComparison]::Ordinal)) `
    -Message 'Preparation must return before the first GitHub release creation mutation.'
Assert-True -Condition (
    $publisher -match [regex]::Escape('[IO.Directory]::Move($resolvedWorkingDirectory, $resolvedPreparedDirectory)') -and
    $publisher -notmatch [regex]::Escape('Move-Item -LiteralPath $resolvedWorkingDirectory')) `
    -Message 'Atomic preparation must fail if the durable destination appears instead of nesting staging beneath it.'
$directoryMoveIndex = $publisher.IndexOf('[IO.Directory]::Move(', [StringComparison]::Ordinal)
$postMoveReadIndex = $publisher.IndexOf('$postMoveState = Read-AndVerifyPreparedState', [StringComparison]::Ordinal)
$postMoveAssertIndex = $publisher.IndexOf(
    'Assert-PreparedDirectory -Directory $resolvedPreparedDirectory -State $postMoveState',
    [StringComparison]::Ordinal)
$preparedReportIndex = $publisher.IndexOf("Mode = 'Prepared'", [StringComparison]::Ordinal)
Assert-True -Condition (
    $directoryMoveIndex -ge 0 -and
    $postMoveReadIndex -gt $directoryMoveIndex -and
    $postMoveAssertIndex -gt $postMoveReadIndex -and
    $preparedReportIndex -gt $postMoveAssertIndex) `
    -Message 'The moved durable directory must be signature/hash verified before preparation reports success.'
Assert-True -Condition ($publisher -match '(?m)^\$Repository\s*=\s*''nativepapaya/package-pilot''\s*$') `
    -Message 'The release signing key must be restricted to the official Package Pilot repository.'
Assert-True -Condition ($publisher -match 'Get-ReleaseHighWaterMark') `
    -Message 'The manual publisher must enforce a monotonically increasing release high-water mark.'
Assert-True -Condition (
    $publisher -match [regex]::Escape('repos/$Repository/releases?per_page=100') -and
    $publisher -match [regex]::Escape('$seenReleaseIds.Add($releaseId)') -and
    $publisher -notmatch "(?s)'release'\s*'list'.*?'--limit'") `
    -Message 'Release high-water checks must paginate the complete unfiltered release history.'
Assert-True -Condition (
    $publisher -match [regex]::Escape('Get-NewerBlockingReleaseRuns') -and
    $publisher -match [regex]::Escape("'--paginate'") -and
    $publisher -match [regex]::Escape("'--slurp'") -and
    $publisher -match [regex]::Escape('runs?per_page=100') -and
    $publisher -match [regex]::Escape('$run.head_branch -eq ''main''') -and
    $publisher -notmatch "(?s)'run'\s*'list'.*?'--limit'\s*'100'") `
    -Message 'Newer-run checks must exhaustively paginate unfiltered workflow runs and filter main locally.'
Assert-True -Condition (
    $publisher -match [regex]::Escape('$seenRunIds.Add($runId)') -and
    $publisher -match [regex]::Escape("pagination was incomplete") -and
    $publisher -match [regex]::Escape('Get-SelectedRun -WorkflowRunId $WorkflowRunId') -and
    $publisher -match [regex]::Escape('[uint64]$currentRun.attempt -ne $RunAttempt')) `
    -Message 'Pagination instability and selected-run reruns must fail closed before publication.'
$matchingArtifactsAssignment = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$matchingArtifacts'
    },
    $true)
Assert-True -Condition (
    $null -ne $matchingArtifactsAssignment -and
    $matchingArtifactsAssignment.Right -is
        [System.Management.Automation.Language.CommandExpressionAst] -and
    $matchingArtifactsAssignment.Right.Expression -is
        [System.Management.Automation.Language.ArrayExpressionAst]) `
    -Message 'Artifact filtering must retain array semantics when exactly one artifact matches under Windows PowerShell strict mode.'
$expectedArtifactName = 'expected-artifact'
foreach ($artifactCount in 0..2) {
    $artifactResponse = [pscustomobject]@{
        artifacts = @(
            for ($index = 0; $index -lt $artifactCount; $index++) {
                [pscustomobject]@{
                    id = $index + 1
                    name = $expectedArtifactName
                    expired = $false
                }
            }
            [pscustomobject]@{
                id = 100
                name = 'other-artifact'
                expired = $false
            }
        )
    }
    $matchingArtifacts = $null
    Invoke-Expression $matchingArtifactsAssignment.Extent.Text
    Assert-True -Condition (
        $matchingArtifacts -is [array] -and
        $matchingArtifacts.Count -eq $artifactCount) `
        -Message "Artifact filtering did not retain array semantics for $artifactCount matching artifacts."
}
$draftPublishIndex = $publisher.IndexOf("'--draft=false'", [StringComparison]::Ordinal)
$draftStaleCheckIndex = if ($draftPublishIndex -ge 0) {
    $publisher.LastIndexOf(
        'Assert-PromotionPreconditionsStillHold',
        $draftPublishIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
$latestPublishIndex = $publisher.LastIndexOf("'--latest'", [StringComparison]::Ordinal)
$latestStaleCheckIndex = if ($latestPublishIndex -ge 0) {
    $publisher.LastIndexOf(
        'Assert-PromotionPreconditionsStillHold',
        $latestPublishIndex,
        [StringComparison]::Ordinal)
}
else {
    -1
}
Assert-True -Condition (
    $draftStaleCheckIndex -ge 0 -and
    $draftStaleCheckIndex -lt $draftPublishIndex -and
    $latestStaleCheckIndex -ge 0 -and
    $latestStaleCheckIndex -lt $latestPublishIndex) `
    -Message 'Promotion must recheck stale run/main/release state immediately before each publish mutation.'
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
Assert-True -Condition (
    $releaseTooling -match [regex]::Escape('New-MsixBundle.ps1') -and
    $releaseTooling -match [regex]::Escape('win-x64') -and
    $releaseTooling -match [regex]::Escape('win-arm64')) `
    -Message 'The hosted Release workflow must build both architectures and create the bundle.'
Assert-True -Condition ($releaseTooling -match [regex]::Escape('PackagePilot.Windows.ReadOnly.dll')) `
    -Message 'The hosted Release workflow must assert the read-only WinGet infrastructure payload.'
foreach ($packageAdminPayload in @(
    'PackagePilot.PackageAdmin.exe'
    'PackagePilot.PackageAdmin.dll'
    'PackagePilot.PackageAdmin.deps.json'
    'PackagePilot.PackageAdmin.runtimeconfig.json'
)) {
    Assert-True -Condition (
        $publisher -match [regex]::Escape($packageAdminPayload) -and
        $releaseTooling -match [regex]::Escape($packageAdminPayload)) `
        -Message "Both release paths must require the privileged package helper payload '$packageAdminPayload'."
}
foreach ($requiredWinGetRuntimePayload in @(
    'Microsoft.Management.Deployment.CsWinRTProjection.dll'
    'Microsoft.Management.Deployment.dll'
    'Microsoft.Management.Deployment.winmd'
)) {
    Assert-True -Condition (
        $publisher -match [regex]::Escape($requiredWinGetRuntimePayload) -and
        $releaseTooling -match [regex]::Escape($requiredWinGetRuntimePayload)) `
        -Message "Both release paths must assert the background WinGet runtime payload '$requiredWinGetRuntimePayload'."
}
Assert-True -Condition ($workflow -match '(?m)^\s*retention-days:\s*30\s*$') `
    -Message 'Unsigned release artifacts must be retained for 30 days.'

$nonExportableFunction = $initializerAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Test-NonExportablePrivateKey'
    },
    $true)
$assertCertificateFunction = $initializerAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-ReleaseCertificate'
    },
    $true)
$getReleaseCertificateFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-ReleaseCertificate'
    },
    $true)
$requiredPayloadAssignment = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -eq 'RequiredApplicationPayloadNames'
    },
    $true)
$getMsixIdentityFromArchiveFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-MsixIdentityFromArchive'
    },
    $true)
$getMsixBundleIdentityFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-MsixBundleIdentity'
    },
    $true)
$assertEmbeddedSignaturesFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-MsixBundleEmbeddedSignatures'
    },
    $true)
$getNewerBlockingRunsFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-NewerBlockingReleaseRuns'
    },
    $true)
$assertPromotionPreconditionsFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-PromotionPreconditionsStillHold'
    },
    $true)
$newPreparedFileRecordFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'New-PreparedFileRecord'
    },
    $true)
$writePreparedStateFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Write-SignedPreparedState'
    },
    $true)
$readPreparedStateFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Read-AndVerifyPreparedState'
    },
    $true)
$forbiddenPowerShellPayloadAssignment = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -eq 'ForbiddenPowerShellPayloadPattern'
    },
    $true)
$getReleaseChecksumLinesFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-ReleaseChecksumLines'
    },
    $true)
$getPowerShell7Function = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-PowerShell7Executable'
    },
    $true)
$assertReleaseJsonSchemaFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-ReleaseJsonSchema'
    },
    $true)
$convertFromReleaseJsonFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'ConvertFrom-ReleaseJson'
    },
    $true)
$assertPreparedDirectoryFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-PreparedDirectory'
    },
    $true)
$assertGitHubAssetsFunction = $publisherAst.Find(
    {
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Assert-GitHubReleaseAssets'
    },
    $true)
Assert-True -Condition (
    $null -ne $nonExportableFunction -and
    $null -ne $assertCertificateFunction -and
    $null -ne $getReleaseCertificateFunction -and
    $null -ne $requiredPayloadAssignment -and
    $null -ne $forbiddenPowerShellPayloadAssignment -and
    $null -ne $getMsixIdentityFromArchiveFunction -and
    $null -ne $getMsixBundleIdentityFunction -and
    $null -ne $assertEmbeddedSignaturesFunction -and
    $null -ne $getNewerBlockingRunsFunction -and
    $null -ne $assertPromotionPreconditionsFunction -and
    $null -ne $newPreparedFileRecordFunction -and
    $null -ne $getReleaseChecksumLinesFunction -and
    $null -ne $writePreparedStateFunction -and
    $null -ne $readPreparedStateFunction -and
    $null -ne $getPowerShell7Function -and
    $null -ne $assertReleaseJsonSchemaFunction -and
    $null -ne $convertFromReleaseJsonFunction -and
    $null -ne $assertPreparedDirectoryFunction -and
    $null -ne $assertGitHubAssetsFunction) `
    -Message 'The certificate validation functions could not be loaded for a runtime compatibility test.'
$writePreparedStateText = $writePreparedStateFunction.Extent.Text
$writeStateIndex = $writePreparedStateText.IndexOf('[IO.File]::WriteAllBytes(', [StringComparison]::Ordinal)
$writeSchemaIndex = $writePreparedStateText.IndexOf('Assert-ReleaseJsonSchema', [StringComparison]::Ordinal)
$signStateIndex = $writePreparedStateText.IndexOf('.SignData(', [StringComparison]::Ordinal)
Assert-True -Condition (
    $writeStateIndex -ge 0 -and
    $writeSchemaIndex -gt $writeStateIndex -and
    $signStateIndex -gt $writeSchemaIndex -and
    $writePreparedStateText -match [regex]::Escape('-Bytes $stateBytes') -and
    $writePreparedStateText -match [regex]::Escape('$stateBytes,')) `
    -Message 'Prepared state must validate and sign the same exact bytes after they are written.'
$readPreparedStateText = $readPreparedStateFunction.Extent.Text
$verifyStateIndex = $readPreparedStateText.IndexOf('.VerifyData(', [StringComparison]::Ordinal)
$readSchemaIndex = $readPreparedStateText.IndexOf('Assert-ReleaseJsonSchema', [StringComparison]::Ordinal)
$parseStateIndex = $readPreparedStateText.IndexOf('ConvertFrom-ReleaseJson', [StringComparison]::Ordinal)
Assert-True -Condition (
    $verifyStateIndex -ge 0 -and
    $readSchemaIndex -gt $verifyStateIndex -and
    $parseStateIndex -gt $readSchemaIndex -and
    $readPreparedStateText -match [regex]::Escape('-Bytes $stateBytes') -and
    $readPreparedStateText -match [regex]::Escape('ConvertFrom-ReleaseJson -Bytes $stateBytes')) `
    -Message 'Prepared state must verify, validate, and parse one exact byte array.'
Assert-True -Condition (
    $assertEmbeddedSignaturesFunction.Extent.Text -match 'Get-AuthenticodeSignature' -and
    $assertEmbeddedSignaturesFunction.Extent.Text -match 'SignerCertificate\.Thumbprint' -and
    $publisher -match '(?s)Assert-MsixBundleEmbeddedSignatures.*?-Path\s+\$packagePath.*?-Certificate\s+\$certificate') `
    -Message 'The publisher must independently verify both extracted embedded package signatures with the exact release certificate.'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Invoke-Expression $nonExportableFunction.Extent.Text
Invoke-Expression $assertCertificateFunction.Extent.Text
Invoke-Expression $getReleaseCertificateFunction.Extent.Text
Invoke-Expression $requiredPayloadAssignment.Extent.Text
Invoke-Expression $forbiddenPowerShellPayloadAssignment.Extent.Text
Invoke-Expression $getMsixIdentityFromArchiveFunction.Extent.Text
Invoke-Expression $getMsixBundleIdentityFunction.Extent.Text
Invoke-Expression $getNewerBlockingRunsFunction.Extent.Text
Invoke-Expression $assertPromotionPreconditionsFunction.Extent.Text
Invoke-Expression $newPreparedFileRecordFunction.Extent.Text
Invoke-Expression $getReleaseChecksumLinesFunction.Extent.Text
Invoke-Expression $writePreparedStateFunction.Extent.Text
Invoke-Expression $readPreparedStateFunction.Extent.Text
Invoke-Expression $getPowerShell7Function.Extent.Text
Invoke-Expression $assertReleaseJsonSchemaFunction.Extent.Text
Invoke-Expression $convertFromReleaseJsonFunction.Extent.Text
Invoke-Expression $assertPreparedDirectoryFunction.Extent.Text
Invoke-Expression $assertGitHubAssetsFunction.Extent.Text
. (Join-Path $buildRoot 'NativeCommand.ps1')
$script:ReleaseJsonValidatorPath = Join-Path $buildRoot 'Test-JsonSchema.ps1'
Assert-True -Condition (
    $getReleaseChecksumLinesFunction.Extent.Text -match 'ForEach-Object\s+-Parallel' -and
    $getReleaseChecksumLinesFunction.Extent.Text -match '-ThrottleLimit\s+4' -and
    $getReleaseChecksumLinesFunction.Extent.Text -match 'Sort-Object\s+Name') `
    -Message 'PowerShell 7 checksum work must be bounded and serialized deterministically by file name.'

$Repository = 'nativepapaya/package-pilot'
$firstWorkflowRunPage = @(1..100 | ForEach-Object {
    [pscustomobject]@{
        id = [uint64](1000 + $_)
        run_number = [uint64](50 + $_)
        head_branch = 'main'
        status = 'completed'
        conclusion = 'failure'
    }
})
$secondWorkflowRunPage = @(
    [pscustomobject]@{
        id = [uint64]2001
        run_number = [uint64]151
        head_branch = 'main'
        status = 'completed'
        conclusion = 'success'
    }
)
$script:TestWorkflowRunPages = @(
    [pscustomobject]@{
        total_count = [uint64]101
        workflow_runs = $firstWorkflowRunPage
    }
    [pscustomobject]@{
        total_count = [uint64]101
        workflow_runs = $secondWorkflowRunPage
    }
)
$script:TestPaginationArguments = $null
function Invoke-GhJson {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $script:TestPaginationArguments = @($Arguments)
    Write-Output -NoEnumerate $script:TestWorkflowRunPages
}

$blockingRunsBeyondFirstPage = @(Get-NewerBlockingReleaseRuns `
    -WorkflowDatabaseId 77 `
    -RunNumber 50)
Assert-True `
    -Condition (
        $blockingRunsBeyondFirstPage.Count -eq 1 -and
        [uint64]$blockingRunsBeyondFirstPage[0].id -eq 2001 -and
        $script:TestPaginationArguments -contains '--paginate' -and
        $script:TestPaginationArguments -contains '--slurp' -and
        ($script:TestPaginationArguments -join ' ') -notmatch 'branch=') `
    -Message 'A blocking successful run beyond the first 100 unfiltered results was not detected.'

function Get-SelectedRun {
    param([Parameter(Mandatory)][uint64]$WorkflowRunId)

    return [pscustomobject]@{
        databaseId = $WorkflowRunId
        workflowDatabaseId = [uint64]77
        number = [uint64]50
        attempt = [uint64]2
        status = 'completed'
        conclusion = 'success'
        headBranch = 'main'
        headSha = '0000000000000000000000000000000000000000'
    }
}

$expectedRerunFailure = $null
try {
    Assert-PromotionPreconditionsStillHold `
        -WorkflowDatabaseId 77 `
        -WorkflowRunId 1001 `
        -RunNumber 50 `
        -RunAttempt 1 `
        -CommitSha '0000000000000000000000000000000000000000' `
        -ReleaseSequence 54 `
        -TagName 'v1.0.54'
}
catch {
    $expectedRerunFailure = $_.Exception.Message
}
Assert-True `
    -Condition ($expectedRerunFailure -like '*selected Release run changed after preparation*') `
    -Message "A selected workflow rerun was not rejected by the final promotion check: '$expectedRerunFailure'"

$Subject = 'CN=PackagePilot.Dev'
$testCertificate = $null
$preparedTestDirectory = $null
$bundleTestDirectory = $null
try {
    $testCertificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Subject `
        -FriendlyName "Package Pilot release security test $([Guid]::NewGuid().ToString('N'))" `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy NonExportable `
        -NotBefore (Get-Date).AddMinutes(-5) `
        -NotAfter (Get-Date).AddMonths(2) `
        -TextExtension @(
            '2.5.29.19={text}'
            '2.5.29.37={text}1.3.6.1.5.5.7.3.3'
        )

    Assert-ReleaseCertificate -Certificate $testCertificate

    $script:CertificateSubject = $Subject
    $script:CertificateFriendlyName = $testCertificate.FriendlyName
    $CertificateThumbprint = $testCertificate.Thumbprint
    $expectedTrustFailure = $null
    try {
        Get-ReleaseCertificate | Out-Null
    }
    catch {
        $expectedTrustFailure = $_.Exception.Message
    }
    Assert-True `
        -Condition ($expectedTrustFailure -like '*not trusted in LocalMachine\TrustedPeople*') `
        -Message "Publisher certificate selection failed before its expected trust boundary: '$expectedTrustFailure'"

    $bundleTestDirectory = Join-Path `
        ([IO.Path]::GetTempPath()) `
        "PackagePilot-bundle-security-test-$([Guid]::NewGuid().ToString('N'))"
    [void][IO.Directory]::CreateDirectory($bundleTestDirectory)

    $collisionSource = Join-Path $bundleTestDirectory 'atomic-source'
    $collisionDestination = Join-Path $bundleTestDirectory 'atomic-destination'
    [void][IO.Directory]::CreateDirectory($collisionSource)
    [void][IO.Directory]::CreateDirectory($collisionDestination)
    [IO.File]::WriteAllText(
        (Join-Path $collisionSource 'marker.txt'),
        'source must remain separate',
        [Text.UTF8Encoding]::new($false))
    $expectedMoveCollision = $null
    try {
        [IO.Directory]::Move($collisionSource, $collisionDestination)
    }
    catch {
        $expectedMoveCollision = $_.Exception.Message
    }
    Assert-True `
        -Condition (
            -not [string]::IsNullOrWhiteSpace($expectedMoveCollision) -and
            (Test-Path -LiteralPath (Join-Path $collisionSource 'marker.txt') -PathType Leaf) -and
            -not (Test-Path `
                -LiteralPath (Join-Path $collisionDestination 'atomic-source') `
                -PathType Container)) `
        -Message 'Atomic staging did not fail cleanly when the durable destination already existed.'

    $validBundlePath = Join-Path $bundleTestDirectory 'valid.msixbundle'
    New-TestMsixBundle -Path $validBundlePath
    $validBundleIdentity = Get-MsixBundleIdentity -Path $validBundlePath
    Assert-True `
        -Condition (
            $validBundleIdentity.EmbeddedPackages.Count -eq 2 -and
            -not $validBundleIdentity.HasSignature -and
            @($validBundleIdentity.EmbeddedPackages | Where-Object HasSignature).Count -eq 0 -and
            ($validBundleIdentity.Architectures -join ',') -eq 'arm64,x64') `
        -Message 'A valid two-architecture bundle did not pass embedded identity and payload validation.'

    $signedBundlePath = Join-Path $bundleTestDirectory 'signed-state.msixbundle'
    New-TestMsixBundle -Path $signedBundlePath -Signed
    $signedBundleIdentity = Get-MsixBundleIdentity -Path $signedBundlePath
    Assert-True `
        -Condition (
            $signedBundleIdentity.HasSignature -and
            @($signedBundleIdentity.EmbeddedPackages | Where-Object HasSignature).Count -eq 2) `
        -Message 'A consistently signed bundle did not retain both embedded signature states.'

    $mixedSignatureBundlePath = Join-Path $bundleTestDirectory 'mixed-signature.msixbundle'
    New-TestMsixBundle `
        -Path $mixedSignatureBundlePath `
        -Signed `
        -OmitArm64Signature
    $expectedMixedSignatureFailure = $null
    try {
        [void](Get-MsixBundleIdentity -Path $mixedSignatureBundlePath)
    }
    catch {
        $expectedMixedSignatureFailure = $_.Exception.Message
    }
    Assert-True `
        -Condition ($expectedMixedSignatureFailure -like '*signature state inconsistent with its bundle*') `
        -Message "A mixed outer/embedded bundle signature state was not rejected: '$expectedMixedSignatureFailure'"

    $extraPackageBundlePath = Join-Path $bundleTestDirectory 'extra-package.msixbundle'
    New-TestMsixBundle -Path $extraPackageBundlePath -PackageCount 3
    $expectedPackageCountFailure = $null
    try {
        [void](Get-MsixBundleIdentity -Path $extraPackageBundlePath)
    }
    catch {
        $expectedPackageCountFailure = $_.Exception.Message
    }
    Assert-True `
        -Condition ($expectedPackageCountFailure -like '*incomplete bundle manifest*') `
        -Message "A bundle with three embedded application packages was not rejected: '$expectedPackageCountFailure'"

    $missingPayloadBundlePath = Join-Path $bundleTestDirectory 'missing-read-only.msixbundle'
    New-TestMsixBundle `
        -Path $missingPayloadBundlePath `
        -OmitArm64ReadOnlyPayload
    $expectedPayloadFailure = $null
    try {
        [void](Get-MsixBundleIdentity -Path $missingPayloadBundlePath)
    }
    catch {
        $expectedPayloadFailure = $_.Exception.Message
    }
    Assert-True `
        -Condition (
            $expectedPayloadFailure -like
                "*missing required application payload 'PackagePilot.Windows.ReadOnly.dll'*") `
        -Message "An embedded package without the read-only WinGet client was not rejected: '$expectedPayloadFailure'"

    $powerShellPayloadBundlePath = Join-Path $bundleTestDirectory 'powershell-runtime.msixbundle'
    New-TestMsixBundle `
        -Path $powerShellPayloadBundlePath `
        -IncludePowerShellPayload
    $expectedPowerShellPayloadFailure = $null
    try {
        [void](Get-MsixBundleIdentity -Path $powerShellPayloadBundlePath)
    }
    catch {
        $expectedPowerShellPayloadFailure = $_.Exception.Message
    }
    Assert-True `
        -Condition ($expectedPowerShellPayloadFailure -like '*contains forbidden PowerShell runtime payload*tools/pwsh.exe*') `
        -Message "An embedded PowerShell runtime payload was not rejected: '$expectedPowerShellPayloadFailure'"

    $wrongArchitectureBundlePath = Join-Path $bundleTestDirectory 'wrong-architecture.msixbundle'
    New-TestMsixBundle `
        -Path $wrongArchitectureBundlePath `
        -Arm64InnerArchitecture x64
    $expectedArchitectureFailure = $null
    try {
        [void](Get-MsixBundleIdentity -Path $wrongArchitectureBundlePath)
    }
    catch {
        $expectedArchitectureFailure = $_.Exception.Message
    }
    Assert-True `
        -Condition ($expectedArchitectureFailure -like '*invalid identity, architecture, or version*') `
        -Message "A manifest-to-embedded-package architecture mismatch was not rejected: '$expectedArchitectureFailure'"

    $PreparedStateFileName = 'prepared-release.json'
    $PreparedStateSignatureFileName = 'prepared-release.json.sig'
    $ReleaseAssetNames = @(
        'PackagePilot.msixbundle'
        'Microsoft.WindowsAppRuntime.2.x64.msix'
        'Microsoft.WindowsAppRuntime.2.arm64.msix'
        'PackagePilot.cer'
        'PackagePilot.appinstaller'
        'SHA256SUMS.txt'
    )
    $preparedTestDirectory = Join-Path `
        ([IO.Path]::GetTempPath()) `
        "PackagePilot-prepared-state-test-$([Guid]::NewGuid().ToString('N'))"
    [void][IO.Directory]::CreateDirectory($preparedTestDirectory)
    foreach ($fileName in @($ReleaseAssetNames + 'release-metadata.json')) {
        [IO.File]::WriteAllText(
            (Join-Path $preparedTestDirectory $fileName),
            "test payload for $fileName",
            [Text.UTF8Encoding]::new($false))
    }
    $checksumTestNames = @(
        'SHA256SUMS.txt'
        'PackagePilot.appinstaller'
        'PackagePilot.cer'
    )
    $checksumTestLines = @(Get-ReleaseChecksumLines `
        -Directory $preparedTestDirectory `
        -FileNames $checksumTestNames)
    $checksumLineNames = @($checksumTestLines | ForEach-Object {
        ($_ -split ' \*', 2)[1]
    })
    Assert-True `
        -Condition (($checksumLineNames -join ',') -eq 'PackagePilot.appinstaller,PackagePilot.cer,SHA256SUMS.txt') `
        -Message 'Release checksum lines were not returned in deterministic file-name order.'
    foreach ($checksumLine in $checksumTestLines) {
        $parts = $checksumLine -split ' \*', 2
        $expectedHash = (Get-FileHash `
            -LiteralPath (Join-Path $preparedTestDirectory $parts[1]) `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-True `
            -Condition ($parts[0] -eq $expectedHash) `
            -Message "Release checksum content was incorrect for '$($parts[1])'."
    }
    $checksumFailureMessage = $null
    try {
        [void](Get-ReleaseChecksumLines `
            -Directory $preparedTestDirectory `
            -FileNames @('z-missing.bin', 'a-missing.bin', 'm-missing.bin'))
    }
    catch {
        $checksumFailureMessage = $_.Exception.Message
    }
    Assert-True `
        -Condition (
            -not [string]::IsNullOrWhiteSpace($checksumFailureMessage) -and
            $checksumFailureMessage.Contains('a-missing.bin') -and
            $checksumFailureMessage.Contains('m-missing.bin') -and
            $checksumFailureMessage.Contains('z-missing.bin') -and
            $checksumFailureMessage.IndexOf('a-missing.bin', [StringComparison]::Ordinal) -lt
                $checksumFailureMessage.IndexOf('m-missing.bin', [StringComparison]::Ordinal) -and
            $checksumFailureMessage.IndexOf('m-missing.bin', [StringComparison]::Ordinal) -lt
                $checksumFailureMessage.IndexOf('z-missing.bin', [StringComparison]::Ordinal)) `
        -Message 'Parallel checksum failures were not reported in deterministic file-name order.'
    $testState = [ordered]@{
        schemaVersion = 1
        repository = 'nativepapaya/package-pilot'
        workflowName = 'Release'
        workflowDatabaseId = [uint64]77
        runId = [uint64]123456789
        runNumber = [uint64]995
        runAttempt = [uint64]1
        artifactId = [uint64]987654321
        artifactName = 'package-pilot-unsigned-123456789-1'
        commitSha = '0123456789abcdef0123456789abcdef01234567'
        releaseSequence = [uint64]999
        tagName = 'v1.0.999'
        packageVersion = '1.0.0.999'
        signerThumbprint = $testCertificate.Thumbprint
        preparedAtUtc = '2026-07-19T12:35:56.1234567+00:00'
        sourceMetadata = New-PreparedFileRecord `
            -Directory $preparedTestDirectory `
            -FileName 'release-metadata.json'
        assets = @($ReleaseAssetNames | ForEach-Object {
            New-PreparedFileRecord -Directory $preparedTestDirectory -FileName $_
        })
    }
    Write-SignedPreparedState `
        -Directory $preparedTestDirectory `
        -State $testState `
        -Certificate $testCertificate
    $verifiedState = Read-AndVerifyPreparedState `
        -Directory $preparedTestDirectory `
        -Certificate $testCertificate
    Assert-PreparedDirectory -Directory $preparedTestDirectory -State $verifiedState

    $remoteAssets = @($verifiedState.assets | ForEach-Object {
        [pscustomobject]@{
            name = [string]$_.name
            size = [uint64]$_.size
            digest = "sha256:$([string]$_.sha256)"
            state = 'uploaded'
        }
    })
    $remoteRelease = [pscustomobject]@{
        tagName = 'v1.0.999'
        assets = @($remoteAssets | Select-Object -Skip 1)
    }
    $missingAssets = @(Assert-GitHubReleaseAssets `
        -Release $remoteRelease `
        -State $verifiedState `
        -AllowMissing)
    Assert-True `
        -Condition ($missingAssets.Count -eq 1 -and $missingAssets[0] -eq $remoteAssets[0].name) `
        -Message 'A matching partial draft did not identify exactly its missing prepared asset.'

    $remoteRelease.assets = @($remoteAssets)
    $remoteRelease.assets[0].digest = 'sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'
    $expectedRemoteMismatch = $null
    try {
        [void](Assert-GitHubReleaseAssets -Release $remoteRelease -State $verifiedState)
    }
    catch {
        $expectedRemoteMismatch = $_.Exception.Message
    }
    Assert-True `
        -Condition ($expectedRemoteMismatch -like '*does not match the signed prepared payload*') `
        -Message "A conflicting remote draft asset was not rejected: '$expectedRemoteMismatch'"

    Add-Content `
        -LiteralPath (Join-Path $preparedTestDirectory 'PackagePilot.appinstaller') `
        -Value 'tampered'
    $expectedTamperFailure = $null
    try {
        Assert-PreparedDirectory -Directory $preparedTestDirectory -State $verifiedState
    }
    catch {
        $expectedTamperFailure = $_.Exception.Message
    }
    Assert-True `
        -Condition ($expectedTamperFailure -like '*no longer matches its signed state*') `
        -Message "Prepared asset tampering was not rejected: '$expectedTamperFailure'"
}
finally {
    if ($null -ne $bundleTestDirectory -and
        (Test-Path -LiteralPath $bundleTestDirectory -PathType Container)) {
        $resolvedBundleTestDirectory = [IO.Path]::GetFullPath($bundleTestDirectory)
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        if ($resolvedBundleTestDirectory.StartsWith(
                $resolvedTemp + '\',
                [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedBundleTestDirectory) -match
                '^PackagePilot-bundle-security-test-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedBundleTestDirectory -Recurse -Force
        }
    }
    if ($null -ne $preparedTestDirectory -and
        (Test-Path -LiteralPath $preparedTestDirectory -PathType Container)) {
        $resolvedPreparedTestDirectory = [IO.Path]::GetFullPath($preparedTestDirectory)
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        if ($resolvedPreparedTestDirectory.StartsWith(
                $resolvedTemp + '\',
                [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedPreparedTestDirectory) -match
                '^PackagePilot-prepared-state-test-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedPreparedTestDirectory -Recurse -Force
        }
    }
    if ($null -ne $testCertificate) {
        Remove-Item `
            -LiteralPath "Cert:\CurrentUser\My\$($testCertificate.Thumbprint)" `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

Write-Output 'Manual release security tests passed.'
