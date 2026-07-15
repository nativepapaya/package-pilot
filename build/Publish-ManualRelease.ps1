#Requires -Version 5.1

[CmdletBinding(DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Prepare')]
    [switch]$Prepare,

    [Parameter(Mandatory, ParameterSetName = 'Promote')]
    [switch]$Promote,

    [Parameter(Mandatory, ParameterSetName = 'Prepare')]
    [Parameter(Mandatory, ParameterSetName = 'Promote')]
    [uint64]$RunId,

    [Parameter(Mandatory, ParameterSetName = 'Prepare')]
    [Parameter(Mandatory, ParameterSetName = 'Promote')]
    [ValidateNotNullOrEmpty()]
    [string]$PreparedDirectory,

    [Parameter(Mandatory, ParameterSetName = 'Promote')]
    [ValidatePattern('^v1\.0\.[0-9]+$')]
    [string]$ConfirmTag,

    [Parameter(Mandatory, ParameterSetName = 'Promote')]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ConfirmBundleSha256,

    [Parameter(ParameterSetName = 'Prepare')]
    [Parameter(ParameterSetName = 'Promote')]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:CertificateSubject = 'CN=PackagePilot.Dev'
$script:CertificateFriendlyName = 'Package Pilot manual release signing'
$script:GhExecutable = $null
$Repository = 'nativepapaya/package-pilot'
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
$RequiredApplicationPayloadNames = @(
    'PackagePilot.App.exe'
    'PackagePilot.App.dll'
    'PackagePilot.App.deps.json'
    'PackagePilot.App.runtimeconfig.json'
    'PackagePilot.Core.dll'
    'PackagePilot.Windows.ReadOnly.dll'
    'PackagePilot.Windows.dll'
    'PackagePilot.Background.exe'
    'PackagePilot.Background.dll'
    'PackagePilot.Background.deps.json'
    'PackagePilot.Background.runtimeconfig.json'
    'PackagePilot.Background.pri'
    'PackagePilot.SourceAdmin.exe'
    'PackagePilot.SourceAdmin.dll'
    'PackagePilot.SourceAdmin.deps.json'
    'PackagePilot.SourceAdmin.runtimeconfig.json'
    'Microsoft.Management.Deployment.CsWinRTProjection.dll'
    'Microsoft.Management.Deployment.dll'
    'Microsoft.Management.Deployment.winmd'
    'Microsoft.Windows.ApplicationModel.Background.Projection.dll'
    'Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll'
)

function Invoke-GhJson {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = & $script:GhExecutable @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $exitCode."
    }

    $json = $output -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($json)) {
        return $null
    }

    return $json | ConvertFrom-Json
}

function Invoke-GhCommand {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $script:GhExecutable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Test-NonExportablePrivateKey {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $privateKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey(
        $Certificate)
    if ($null -eq $privateKey) {
        return $false
    }

    try {
        if ($privateKey -is [System.Security.Cryptography.RSACng]) {
            return $privateKey.Key.ExportPolicy -eq
                [System.Security.Cryptography.CngExportPolicies]::None
        }

        if ($privateKey -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            return -not $privateKey.CspKeyContainerInfo.Exportable
        }

        return $false
    }
    finally {
        $privateKey.Dispose()
    }
}

function Get-ReleaseCertificate {
    $matches = @(
        if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
            Get-ChildItem 'Cert:\CurrentUser\My' |
                Where-Object {
                    $_.FriendlyName -eq $script:CertificateFriendlyName -and
                    $_.Subject -eq $script:CertificateSubject -and
                    $_.HasPrivateKey
                }
        }
        else {
            Get-ChildItem 'Cert:\CurrentUser\My' |
                Where-Object {
                    $_.Thumbprint -eq $CertificateThumbprint -and
                    $_.FriendlyName -eq $script:CertificateFriendlyName
                }
        }
    )

    if ($matches.Count -ne 1) {
        throw "Exactly one '$($script:CertificateFriendlyName)' private key is required. Run Initialize-ManualReleaseCertificate.ps1 from an elevated PowerShell window."
    }

    $certificate = $matches[0]
    if ($certificate.Subject -ne $script:CertificateSubject -or
        $certificate.Issuer -ne $script:CertificateSubject) {
        throw "The release certificate must be self-signed as '$($script:CertificateSubject)'."
    }
    if (-not $certificate.HasPrivateKey) {
        throw 'The release certificate does not contain a private key.'
    }
    if ($certificate.NotBefore -gt (Get-Date) -or
        $certificate.NotAfter -le (Get-Date).AddDays(30)) {
        throw 'The release certificate is not yet valid, is expired, or expires within 30 days.'
    }
    if ($certificate.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1' -or
        $certificate.PublicKey.Key.KeySize -lt 3072) {
        throw 'The release certificate must use an RSA private key of at least 3072 bits.'
    }

    $enhancedKeyUsages = @(
        $certificate.EnhancedKeyUsageList |
            ForEach-Object { [string]$_.ObjectId }
    )
    if ($enhancedKeyUsages.Count -ne 1 -or
        $enhancedKeyUsages[0] -ne '1.3.6.1.5.5.7.3.3') {
        throw 'The release certificate must be restricted to code signing.'
    }
    $keyUsage = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.15' } |
        Select-Object -First 1
    if ($null -eq $keyUsage -or
        $keyUsage.KeyUsages -ne
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) {
        throw 'The release certificate must be restricted to digital signatures.'
    }
    $basicConstraints = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
        Select-Object -First 1
    if ($null -eq $basicConstraints -or $basicConstraints.CertificateAuthority) {
        throw 'The release certificate must be an end-entity certificate, not a certificate authority.'
    }
    if (-not (Test-NonExportablePrivateKey -Certificate $certificate)) {
        throw 'The release certificate private key is exportable or its non-exportable policy cannot be verified.'
    }

    $trustedPath = "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
    if (-not (Test-Path -LiteralPath $trustedPath)) {
        throw 'The release certificate is not trusted in LocalMachine\TrustedPeople. Run Initialize-ManualReleaseCertificate.ps1 from an elevated PowerShell window.'
    }

    return $certificate
}

function Get-MsixIdentityFromArchive {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$DisplayName,

        [switch]$RequireApplicationPayload
    )

    $manifestEntry = $Archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) {
        throw "'$DisplayName' does not contain AppxManifest.xml."
    }

    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try {
        [xml]$manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $identity = $manifest.Package.Identity
    if ($null -eq $identity) {
        throw "'$DisplayName' does not contain a package identity."
    }
    if ($RequireApplicationPayload) {
        foreach ($entryName in $RequiredApplicationPayloadNames) {
            if ($null -eq $Archive.GetEntry($entryName)) {
                throw "'$DisplayName' is missing required application payload '$entryName'."
            }
        }
    }

    return [pscustomobject]@{
        Name = [string]$identity.Name
        Publisher = [string]$identity.Publisher
        Version = [string]$identity.Version
        ProcessorArchitecture = ([string]$identity.ProcessorArchitecture).ToLowerInvariant()
        HasSignature = $null -ne $Archive.GetEntry('AppxSignature.p7x')
    }
}

function Get-MsixIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return Get-MsixIdentityFromArchive -Archive $archive -DisplayName $Path
    }
    finally {
        $archive.Dispose()
    }
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
        if ($null -eq $identity -or $packages.Count -ne 2) {
            throw "'$Path' contains an incomplete bundle manifest."
        }

        $embeddedMsixEntries = @($archive.Entries | Where-Object {
            [IO.Path]::GetExtension($_.FullName) -ieq '.msix'
        })
        if ($embeddedMsixEntries.Count -ne 2) {
            throw "'$Path' must contain exactly two embedded application MSIX files."
        }
        $bundleHasSignature = $null -ne $archive.GetEntry('AppxSignature.p7x')

        $embeddedPackages = foreach ($package in $packages) {
            $fileName = $package.GetAttribute('FileName')
            $manifestArchitecture = $package.GetAttribute('Architecture').ToLowerInvariant()
            $manifestVersion = $package.GetAttribute('Version')
            if (-not [string]::Equals(
                    $package.GetAttribute('Type'),
                    'application',
                    [StringComparison]::OrdinalIgnoreCase) -or
                [string]::IsNullOrWhiteSpace($fileName) -or
                [IO.Path]::GetFileName($fileName) -ne $fileName -or
                [IO.Path]::GetExtension($fileName) -ine '.msix') {
                throw "'$Path' contains a non-application or unsafe embedded package record."
            }

            $embeddedEntry = $archive.GetEntry($fileName)
            if ($null -eq $embeddedEntry) {
                throw "'$Path' bundle manifest references missing embedded package '$fileName'."
            }

            $memory = [IO.MemoryStream]::new()
            $entryStream = $embeddedEntry.Open()
            try {
                $entryStream.CopyTo($memory)
            }
            finally {
                $entryStream.Dispose()
            }
            $memory.Position = 0
            $embeddedArchive = [IO.Compression.ZipArchive]::new(
                $memory,
                [IO.Compression.ZipArchiveMode]::Read,
                $false)
            try {
                $embeddedIdentity = Get-MsixIdentityFromArchive `
                    -Archive $embeddedArchive `
                    -DisplayName "${Path}::$fileName" `
                    -RequireApplicationPayload
            }
            finally {
                $embeddedArchive.Dispose()
                $memory.Dispose()
            }

            if ($embeddedIdentity.Name -ne 'PackagePilot.Desktop' -or
                $embeddedIdentity.Publisher -ne 'CN=PackagePilot.Dev' -or
                $embeddedIdentity.Version -ne $identity.GetAttribute('Version') -or
                $embeddedIdentity.Version -ne $manifestVersion -or
                $embeddedIdentity.ProcessorArchitecture -ne $manifestArchitecture) {
                throw "'${Path}::$fileName' has an invalid identity, architecture, or version."
            }
            if ([bool]$embeddedIdentity.HasSignature -ne $bundleHasSignature) {
                throw "'${Path}::$fileName' has a signature state inconsistent with its bundle."
            }

            [pscustomobject]@{
                FileName = $fileName
                Name = $embeddedIdentity.Name
                Publisher = $embeddedIdentity.Publisher
                Version = $embeddedIdentity.Version
                Architecture = $embeddedIdentity.ProcessorArchitecture
                HasSignature = [bool]$embeddedIdentity.HasSignature
            }
        }

        $referencedFileNames = @($embeddedPackages.FileName | Sort-Object)
        $actualFileNames = @($embeddedMsixEntries.FullName | Sort-Object)
        $architectures = @($embeddedPackages.Architecture | Sort-Object -Unique)
        if (($referencedFileNames -join "`n") -ne ($actualFileNames -join "`n") -or
            ($architectures -join ',') -ne 'arm64,x64') {
            throw "'$Path' does not contain exactly one validated x64 and ARM64 application package."
        }

        return [pscustomobject]@{
            Name = $identity.GetAttribute('Name')
            Publisher = $identity.GetAttribute('Publisher')
            Version = $identity.GetAttribute('Version')
            Architectures = $architectures
            EmbeddedPackages = @($embeddedPackages)
            HasSignature = $bundleHasSignature
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-MsixBundleEmbeddedSignatures {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Identity,

        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $embeddedPackages = @($Identity.EmbeddedPackages)
    if (-not [bool]$Identity.HasSignature -or $embeddedPackages.Count -ne 2) {
        throw 'The signed bundle does not expose exactly two signed embedded packages.'
    }

    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $validationDirectory = [IO.Path]::GetFullPath((Join-Path `
        $temporaryRoot `
        "PackagePilot-BundleSignatureVerify-$([Guid]::NewGuid().ToString('N'))"))
    if (-not $validationDirectory.StartsWith(
            $temporaryRoot + '\',
            [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($validationDirectory) -notmatch
            '^PackagePilot-BundleSignatureVerify-[0-9a-f]{32}$') {
        throw "The embedded-signature validation directory resolved outside the temporary directory."
    }

    [void][IO.Directory]::CreateDirectory($validationDirectory)
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            foreach ($package in $embeddedPackages) {
                $fileName = [string]$package.FileName
                if ([string]::IsNullOrWhiteSpace($fileName) -or
                    [IO.Path]::GetFileName($fileName) -ne $fileName -or
                    [IO.Path]::GetExtension($fileName) -ine '.msix') {
                    throw "The signed bundle contains an unsafe embedded package name."
                }

                $entry = $archive.GetEntry($fileName)
                if ($null -eq $entry) {
                    throw "The signed bundle is missing embedded package '$fileName'."
                }

                [IO.Compression.ZipFileExtensions]::ExtractToFile(
                    $entry,
                    (Join-Path $validationDirectory $fileName),
                    $false)
            }
        }
        finally {
            $archive.Dispose()
        }

        foreach ($package in $embeddedPackages) {
            $fileName = [string]$package.FileName
            $signature = Get-AuthenticodeSignature `
                -FilePath (Join-Path $validationDirectory $fileName)
            if ($signature.Status -ne
                    [System.Management.Automation.SignatureStatus]::Valid -or
                $null -eq $signature.SignerCertificate -or
                -not [string]::Equals(
                    $signature.SignerCertificate.Thumbprint,
                    $Certificate.Thumbprint,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Embedded package '$fileName' failed signer verification: $($signature.StatusMessage)"
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $validationDirectory -PathType Container) {
            $cleanupPath = [IO.Path]::GetFullPath($validationDirectory)
            if (-not $cleanupPath.StartsWith(
                    $temporaryRoot + '\',
                    [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($cleanupPath) -notmatch
                    '^PackagePilot-BundleSignatureVerify-[0-9a-f]{32}$') {
                throw "Refusing to clean unsafe embedded-signature validation path '$cleanupPath'."
            }
            Remove-Item -LiteralPath $cleanupPath -Recurse -Force
        }
    }
}

function Assert-MetadataProperty {
    param(
        [Parameter(Mandatory)]
        [object]$Metadata,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($Metadata.PSObject.Properties.Name -notcontains $Name) {
        throw "release-metadata.json is missing '$Name'."
    }
}

function Get-ExistingReleases {
    $pageResult = Invoke-GhJson -Arguments @(
        'api'
        '--paginate'
        '--slurp'
        "repos/$Repository/releases?per_page=100"
    )
    $rawReleases = @($pageResult | ForEach-Object { $_ })
    $seenReleaseIds = [Collections.Generic.HashSet[uint64]]::new()
    $releases = [Collections.Generic.List[object]]::new()
    foreach ($release in $rawReleases) {
        if ($null -eq $release) {
            throw 'GitHub returned a malformed release page.'
        }
        foreach ($propertyName in @('id', 'tag_name', 'draft', 'prerelease')) {
            if ($release.PSObject.Properties.Name -notcontains $propertyName) {
                throw "GitHub returned a release without '$propertyName'."
            }
        }

        [uint64]$releaseId = 0
        if (-not [uint64]::TryParse(
                [string]$release.id,
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$releaseId) -or
            $releaseId -lt 1) {
            throw 'GitHub returned an invalid release identifier.'
        }
        if (-not $seenReleaseIds.Add($releaseId)) {
            throw 'GitHub returned duplicate releases during pagination; retry instead of publishing from an unstable snapshot.'
        }

        $releases.Add([pscustomobject]@{
            tagName = [string]$release.tag_name
            isDraft = [bool]$release.draft
            isPrerelease = [bool]$release.prerelease
        })
    }

    return @($releases)
}

function Get-ReleaseHighWaterMark {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Releases
    )

    [uint64]$highWaterMark = 4
    foreach ($release in $Releases) {
        $tagName = [string]$release.tagName
        if ($tagName -notmatch '^v1\.0\.([0-9]+)$') {
            continue
        }

        [uint64]$sequence = 0
        if (-not [uint64]::TryParse(
                $Matches[1],
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$sequence)) {
            throw "Release tag '$tagName' contains an invalid sequence."
        }
        if ($sequence -gt $highWaterMark) {
            $highWaterMark = $sequence
        }
    }

    return $highWaterMark
}

function Get-ExactTagReference {
    param(
        [Parameter(Mandatory)]
        [string]$TagName
    )

    $references = Invoke-GhJson -Arguments @(
        'api'
        "repos/$Repository/git/matching-refs/tags/$TagName"
    )
    return @($references) |
        Where-Object { [string]$_.ref -eq "refs/tags/$TagName" } |
        Select-Object -First 1
}

function Get-SelectedRun {
    param(
        [Parameter(Mandatory)]
        [uint64]$WorkflowRunId
    )

    if ($WorkflowRunId -lt 1) {
        throw 'An exact positive Release workflow run ID is required.'
    }

    return Invoke-GhJson -Arguments @(
        'run'
        'view'
        $WorkflowRunId.ToString()
        '--repo'
        $Repository
        '--json'
        'attempt,conclusion,databaseId,event,headBranch,headSha,number,status,workflowDatabaseId,workflowName'
    )
}

function Get-NewerBlockingReleaseRuns {
    param(
        [Parameter(Mandatory)]
        [uint64]$WorkflowDatabaseId,

        [Parameter(Mandatory)]
        [uint64]$RunNumber
    )

    # Do not add branch/status filters here. GitHub caps filtered workflow-run
    # searches at 1,000 results, which could hide an older blocking run. Fetch
    # every page for this workflow and filter the main branch locally instead.
    $pageResult = Invoke-GhJson -Arguments @(
        'api'
        '--paginate'
        '--slurp'
        "repos/$Repository/actions/workflows/$WorkflowDatabaseId/runs?per_page=100"
    )
    # Windows PowerShell 5.1 can preserve a ConvertFrom-Json top-level array as
    # one pipeline object. Enumerate that array explicitly, but not the page
    # objects or their workflow_runs arrays.
    $pages = @($pageResult | ForEach-Object { $_ })
    if ($pages.Count -eq 0) {
        throw 'GitHub returned no workflow-run pages; refusing to infer that no newer run exists.'
    }

    [uint64]$expectedTotalCount = 0
    $hasExpectedTotalCount = $false
    $seenRunIds = [Collections.Generic.HashSet[uint64]]::new()
    $blockingRuns = [Collections.Generic.List[object]]::new()
    foreach ($page in $pages) {
        if ($null -eq $page -or
            $page.PSObject.Properties.Name -notcontains 'total_count' -or
            $page.PSObject.Properties.Name -notcontains 'workflow_runs') {
            throw 'GitHub returned a malformed workflow-run page.'
        }

        [uint64]$pageTotalCount = 0
        if (-not [uint64]::TryParse(
                [string]$page.total_count,
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$pageTotalCount)) {
            throw 'GitHub returned an invalid workflow-run total count.'
        }
        if (-not $hasExpectedTotalCount) {
            $expectedTotalCount = $pageTotalCount
            $hasExpectedTotalCount = $true
        }
        elseif ($expectedTotalCount -ne $pageTotalCount) {
            throw 'The workflow-run list changed during pagination; retry after GitHub settles.'
        }

        foreach ($run in @($page.workflow_runs | Where-Object { $null -ne $_ })) {
            foreach ($propertyName in @(
                'id',
                'run_number',
                'head_branch',
                'status',
                'conclusion')) {
                if ($run.PSObject.Properties.Name -notcontains $propertyName) {
                    throw "GitHub returned a workflow run without '$propertyName'."
                }
            }

            [uint64]$runId = 0
            [uint64]$candidateRunNumber = 0
            if (-not [uint64]::TryParse(
                    [string]$run.id,
                    [Globalization.NumberStyles]::None,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [ref]$runId) -or
                $runId -lt 1 -or
                -not [uint64]::TryParse(
                    [string]$run.run_number,
                    [Globalization.NumberStyles]::None,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [ref]$candidateRunNumber) -or
                $candidateRunNumber -lt 1) {
                throw 'GitHub returned an invalid workflow run identifier or number.'
            }
            if (-not $seenRunIds.Add($runId)) {
                throw 'GitHub returned duplicate workflow runs during pagination; retry instead of publishing from an unstable snapshot.'
            }

            if ([string]$run.head_branch -eq 'main' -and
                $candidateRunNumber -gt $RunNumber -and
                ([string]$run.status -ne 'completed' -or
                    [string]$run.conclusion -eq 'success')) {
                $blockingRuns.Add($run)
            }
        }
    }

    if (-not $hasExpectedTotalCount -or
        [uint64]$seenRunIds.Count -ne $expectedTotalCount) {
        throw 'GitHub workflow-run pagination was incomplete; refusing to publish.'
    }

    return @($blockingRuns)
}

function New-PreparedFileRecord {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [string]$FileName
    )

    $path = Join-Path $Directory $FileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Prepared release file '$FileName' is missing."
    }

    $file = Get-Item -LiteralPath $path
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    return [ordered]@{
        name = $FileName
        size = [uint64]$file.Length
        sha256 = $hash.Hash.ToLowerInvariant()
    }
}

function Write-SignedPreparedState {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [object]$State,

        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $statePath = Join-Path $Directory $PreparedStateFileName
    $signaturePath = Join-Path $Directory $PreparedStateSignatureFileName
    if ((Test-Path -LiteralPath $statePath) -or (Test-Path -LiteralPath $signaturePath)) {
        throw 'The prepared release state or signature already exists.'
    }

    $json = $State | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText(
        $statePath,
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    $privateKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey(
        $Certificate)
    if ($null -eq $privateKey) {
        throw 'The release certificate RSA private key could not sign the prepared state.'
    }

    try {
        $signature = $privateKey.SignData(
            [IO.File]::ReadAllBytes($statePath),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        [IO.File]::WriteAllText(
            $signaturePath,
            [Convert]::ToBase64String($signature) + [Environment]::NewLine,
            [Text.Encoding]::ASCII)
    }
    finally {
        $privateKey.Dispose()
    }
}

function Read-AndVerifyPreparedState {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    $statePath = Join-Path $Directory $PreparedStateFileName
    $signaturePath = Join-Path $Directory $PreparedStateSignatureFileName
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) {
        throw 'The prepared release state or its detached signature is missing.'
    }

    try {
        $signature = [Convert]::FromBase64String(
            (Get-Content -LiteralPath $signaturePath -Raw).Trim())
    }
    catch {
        throw 'The prepared release state signature is not valid Base64.'
    }

    $publicKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey(
        $Certificate)
    if ($null -eq $publicKey) {
        throw 'The release certificate RSA public key could not verify the prepared state.'
    }

    try {
        $isValid = $publicKey.VerifyData(
            [IO.File]::ReadAllBytes($statePath),
            $signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    }
    finally {
        $publicKey.Dispose()
    }
    if (-not $isValid) {
        throw 'The prepared release state signature is invalid.'
    }

    return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}

function Assert-PreparedDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [object]$State
    )

    $expectedNames = @(
        $ReleaseAssetNames
        'release-metadata.json'
        $PreparedStateFileName
        $PreparedStateSignatureFileName
    ) | Sort-Object
    $directories = @(Get-ChildItem -LiteralPath $Directory -Directory -Force)
    $actualNames = @(Get-ChildItem -LiteralPath $Directory -File -Force |
        ForEach-Object Name |
        Sort-Object)
    if ($directories.Count -ne 0 -or
        ($actualNames -join "`n") -ne ($expectedNames -join "`n")) {
        throw 'The prepared release directory contains missing, duplicate, nested, or unexpected content.'
    }

    $stateAssets = @($State.assets)
    $stateAssetNames = @($stateAssets | ForEach-Object { [string]$_.name } | Sort-Object)
    $expectedAssetNames = @($ReleaseAssetNames | Sort-Object)
    if ($stateAssets.Count -ne $ReleaseAssetNames.Count -or
        ($stateAssetNames -join "`n") -ne ($expectedAssetNames -join "`n")) {
        throw 'The prepared release state does not contain the exact release asset set.'
    }

    foreach ($record in $stateAssets) {
        $current = New-PreparedFileRecord -Directory $Directory -FileName ([string]$record.name)
        if ([uint64]$record.size -ne [uint64]$current.size -or
            [string]$record.sha256 -ne [string]$current.sha256) {
            throw "Prepared release asset '$([string]$record.name)' no longer matches its signed state."
        }
    }

    $sourceMetadata = New-PreparedFileRecord -Directory $Directory -FileName 'release-metadata.json'
    if ([uint64]$State.sourceMetadata.size -ne [uint64]$sourceMetadata.size -or
        [string]$State.sourceMetadata.sha256 -ne [string]$sourceMetadata.sha256) {
        throw 'release-metadata.json no longer matches the signed prepared state.'
    }
}

function Get-ReleaseView {
    param(
        [Parameter(Mandatory)]
        [string]$TagName
    )

    return Invoke-GhJson -Arguments @(
        'release'
        'view'
        $TagName
        '--repo'
        $Repository
        '--json'
        'assets,isDraft,isPrerelease,name,tagName,targetCommitish,url'
    )
}

function Assert-GitHubReleaseAssets {
    param(
        [Parameter(Mandatory)]
        [object]$Release,

        [Parameter(Mandatory)]
        [object]$State,

        [switch]$AllowMissing
    )

    $stateAssets = @{}
    foreach ($record in @($State.assets)) {
        $stateAssets[[string]$record.name] = $record
    }

    $seen = @{}
    foreach ($asset in @($Release.assets)) {
        $name = [string]$asset.name
        if (-not $stateAssets.ContainsKey($name) -or $seen.ContainsKey($name)) {
            throw "GitHub release '$([string]$Release.tagName)' contains unexpected or duplicate asset '$name'."
        }

        $expected = $stateAssets[$name]
        $expectedDigest = "sha256:$([string]$expected.sha256)"
        if ([uint64]$asset.size -ne [uint64]$expected.size -or
            [string]$asset.digest -ne $expectedDigest -or
            [string]$asset.state -ne 'uploaded') {
            throw "GitHub release asset '$name' does not match the signed prepared payload."
        }
        $seen[$name] = $true
    }

    $missing = @($ReleaseAssetNames | Where-Object { -not $seen.ContainsKey($_) })
    if (-not $AllowMissing -and $missing.Count -ne 0) {
        throw "GitHub release is missing prepared assets: $($missing -join ', ')."
    }

    return $missing
}

function Assert-GitHubReleaseBinding {
    param(
        [Parameter(Mandatory)]
        [object]$Release,

        [Parameter(Mandatory)]
        [string]$TagName,

        [Parameter(Mandatory)]
        [string]$CommitSha
    )

    if ([string]$Release.tagName -ne $TagName -or
        [string]$Release.name -ne "Package Pilot $($TagName.Substring(1))" -or
        [bool]$Release.isPrerelease -or
        -not [string]::Equals(
            [string]$Release.targetCommitish,
            $CommitSha,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release '$TagName' does not match the signed prepared state."
    }

    $tag = Get-ExactTagReference -TagName $TagName
    if ($null -ne $tag -and
        ([string]$tag.object.type -ne 'commit' -or
            -not [string]::Equals(
                [string]$tag.object.sha,
                $CommitSha,
                [StringComparison]::OrdinalIgnoreCase))) {
        throw "Git tag '$TagName' does not point to the signed prepared commit."
    }
}

function Assert-PromotionPreconditionsStillHold {
    param(
        [Parameter(Mandatory)]
        [uint64]$WorkflowDatabaseId,

        [Parameter(Mandatory)]
        [uint64]$WorkflowRunId,

        [Parameter(Mandatory)]
        [uint64]$RunNumber,

        [Parameter(Mandatory)]
        [uint64]$RunAttempt,

        [Parameter(Mandatory)]
        [string]$CommitSha,

        [Parameter(Mandatory)]
        [uint64]$ReleaseSequence,

        [Parameter(Mandatory)]
        [string]$TagName
    )

    $currentRun = Get-SelectedRun -WorkflowRunId $WorkflowRunId
    if ([uint64]$currentRun.databaseId -ne $WorkflowRunId -or
        [uint64]$currentRun.workflowDatabaseId -ne $WorkflowDatabaseId -or
        [uint64]$currentRun.number -ne $RunNumber -or
        [uint64]$currentRun.attempt -ne $RunAttempt -or
        [string]$currentRun.status -ne 'completed' -or
        [string]$currentRun.conclusion -ne 'success' -or
        [string]$currentRun.headBranch -ne 'main' -or
        -not [string]::Equals(
            [string]$currentRun.headSha,
            $CommitSha,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The selected Release run changed after preparation; the draft remains unpublished.'
    }

    $currentMain = Invoke-GhJson -Arguments @(
        'api'
        "repos/$Repository/branches/main"
    )
    if (-not [string]::Equals(
            [string]$currentMain.commit.sha,
            $CommitSha,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Main changed after preparation; the draft remains unpublished for inspection.'
    }

    $blockingRuns = @(Get-NewerBlockingReleaseRuns `
        -WorkflowDatabaseId $WorkflowDatabaseId `
        -RunNumber $RunNumber)
    if ($blockingRuns.Count -ne 0) {
        throw 'A newer successful or unfinished Release run appeared; the draft remains unpublished.'
    }

    $otherReleases = @(Get-ExistingReleases | Where-Object {
        [string]$_.tagName -ne $TagName
    })
    [uint64]$highWaterMark = Get-ReleaseHighWaterMark -Releases $otherReleases
    if ($ReleaseSequence -le $highWaterMark) {
        throw "A newer v1.0.$highWaterMark release appeared; the draft remains unpublished."
    }
}

$ghCommand = Get-Command 'gh.exe' -ErrorAction SilentlyContinue
if ($null -eq $ghCommand) {
    $ghCommand = Get-Command 'gh' -ErrorAction SilentlyContinue
}
if ($null -eq $ghCommand) {
    throw 'GitHub CLI (gh) is required. Install it and run gh auth login.'
}
$script:GhExecutable = $ghCommand.Source

& $script:GhExecutable 'auth' 'status' '--hostname' 'github.com' *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI is not authenticated. Run gh auth login before publishing.'
}

$workflowDefinition = Invoke-GhJson -Arguments @(
    'api'
    "repos/$Repository/actions/workflows/release.yml"
)
if ($null -eq $workflowDefinition -or
    [string]$workflowDefinition.path -ne '.github/workflows/release.yml' -or
    [string]$workflowDefinition.state -ne 'active') {
    throw "The active .github/workflows/release.yml workflow could not be verified in '$Repository'."
}

$resolvedPreparedDirectory = [IO.Path]::GetFullPath(
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PreparedDirectory))
$certificate = Get-ReleaseCertificate
$preparedState = $null
if ($Promote) {
    if (-not (Test-Path -LiteralPath $resolvedPreparedDirectory -PathType Container)) {
        throw "PreparedDirectory '$resolvedPreparedDirectory' was not found."
    }
    $preparedState = Read-AndVerifyPreparedState `
        -Directory $resolvedPreparedDirectory `
        -Certificate $certificate
    foreach ($propertyName in @(
        'schemaVersion'
        'repository'
        'workflowName'
        'workflowDatabaseId'
        'runId'
        'runNumber'
        'runAttempt'
        'artifactId'
        'artifactName'
        'commitSha'
        'releaseSequence'
        'tagName'
        'packageVersion'
        'signerThumbprint'
        'preparedAtUtc'
        'sourceMetadata'
        'assets'
    )) {
        Assert-MetadataProperty -Metadata $preparedState -Name $propertyName
    }
    if ([int]$preparedState.schemaVersion -ne 1 -or
        [string]$preparedState.repository -ne $Repository -or
        [string]$preparedState.workflowName -ne 'Release' -or
        -not [string]::Equals(
            [string]$preparedState.signerThumbprint,
            $certificate.Thumbprint,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The signed prepared state has an invalid schema, repository, workflow, or signer.'
    }
    if ([uint64]$preparedState.runId -ne $RunId) {
        throw 'The supplied RunId does not match the signed prepared state.'
    }
    $preparedBundleRecord = @($preparedState.assets) |
        Where-Object { [string]$_.name -eq 'PackagePilot.msixbundle' } |
        Select-Object -First 1
    if ($null -eq $preparedBundleRecord -or
        -not [string]::Equals(
            [string]$preparedBundleRecord.sha256,
            $ConfirmBundleSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ConfirmBundleSha256 does not match the exact signed prepared bundle.'
    }
    Assert-PreparedDirectory -Directory $resolvedPreparedDirectory -State $preparedState
}
elseif (Test-Path -LiteralPath $resolvedPreparedDirectory) {
    throw "PreparedDirectory '$resolvedPreparedDirectory' already exists. Preparation never overwrites or resumes a directory."
}

$selectedRun = Get-SelectedRun -WorkflowRunId $RunId
if ($null -eq $selectedRun) {
    throw 'The selected GitHub Actions run could not be read.'
}
if ([string]$selectedRun.status -ne 'completed' -or
    [string]$selectedRun.conclusion -ne 'success' -or
    [string]$selectedRun.headBranch -ne 'main' -or
    [string]$selectedRun.workflowName -ne 'Release' -or
    [uint64]$selectedRun.workflowDatabaseId -ne [uint64]$workflowDefinition.id -or
    @('push', 'workflow_dispatch') -notcontains [string]$selectedRun.event) {
    throw 'The selected workflow run must be a successful completed Release run from main.'
}

$mainBranch = Invoke-GhJson -Arguments @(
    'api'
    "repos/$Repository/branches/main"
)
$mainCommitSha = [string]$mainBranch.commit.sha
if ($mainCommitSha -notmatch '^[0-9a-fA-F]{40}$' -or
    [string]$selectedRun.headSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'The selected run or main branch has an invalid commit SHA.'
}
$ancestry = Invoke-GhJson -Arguments @(
    'api'
    "repos/$Repository/compare/$([string]$selectedRun.headSha)...$mainCommitSha"
)
if (@('ahead', 'identical') -notcontains [string]$ancestry.status -or
    -not [string]::Equals(
        [string]$ancestry.merge_base_commit.sha,
        [string]$selectedRun.headSha,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The selected workflow commit is not an ancestor of the current main branch.'
}
if (-not [string]::Equals(
        [string]$selectedRun.headSha,
        $mainCommitSha,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The selected workflow commit is no longer the exact current main commit.'
}

[uint64]$selectedRunId = [uint64]$selectedRun.databaseId
[uint64]$selectedRunNumber = [uint64]$selectedRun.number
[uint64]$selectedRunAttempt = [uint64]$selectedRun.attempt
[uint64]$expectedSequence = $selectedRunNumber + 4
[uint64]$expectedBuild = [Math]::Floor($expectedSequence / 65536)
[uint64]$expectedRevision = $expectedSequence % 65536
if ($expectedBuild -gt [uint16]::MaxValue) {
    throw "Release sequence $expectedSequence exceeds the MSIX version capacity."
}

$expectedVersion = "1.0.$expectedBuild.$expectedRevision"
$expectedTag = "v1.0.$expectedSequence"
$expectedArtifactName = "package-pilot-unsigned-$selectedRunId-$selectedRunAttempt"
if ($Promote -and $ConfirmTag -ne $expectedTag) {
    throw "ConfirmTag '$ConfirmTag' does not match the exact prepared release '$expectedTag'."
}
$newerBlockingRuns = @(Get-NewerBlockingReleaseRuns `
    -WorkflowDatabaseId ([uint64]$workflowDefinition.id) `
    -RunNumber $selectedRunNumber)
if ($newerBlockingRuns.Count -ne 0) {
    throw 'A newer successful or unfinished main-branch Release workflow run exists. Prepare the newest intended successful run instead.'
}

if ($Promote) {
    if ([uint64]$preparedState.workflowDatabaseId -ne [uint64]$workflowDefinition.id -or
        [uint64]$preparedState.runId -ne $selectedRunId -or
        [uint64]$preparedState.runNumber -ne $selectedRunNumber -or
        [uint64]$preparedState.runAttempt -ne $selectedRunAttempt -or
        [string]$preparedState.artifactName -ne $expectedArtifactName -or
        [string]$preparedState.commitSha -ne ([string]$selectedRun.headSha).ToLowerInvariant() -or
        [uint64]$preparedState.releaseSequence -ne $expectedSequence -or
        [string]$preparedState.tagName -ne $expectedTag -or
        [string]$preparedState.packageVersion -ne $expectedVersion) {
        throw 'The signed prepared state no longer matches the exact selected workflow run.'
    }
}

$existingReleases = @(Get-ExistingReleases)
$existingReleaseSummary = $existingReleases |
    Where-Object { [string]$_.tagName -eq $expectedTag } |
    Select-Object -First 1
$otherReleases = @($existingReleases | Where-Object { [string]$_.tagName -ne $expectedTag })
[uint64]$releaseHighWaterMark = Get-ReleaseHighWaterMark -Releases $otherReleases
if ($expectedSequence -le $releaseHighWaterMark) {
    throw "Release '$expectedTag' is not newer than the existing v1.0.$releaseHighWaterMark high-water mark."
}
$existingTag = Get-ExactTagReference -TagName $expectedTag
if ($Prepare) {
    if ($null -ne $existingReleaseSummary -or $null -ne $existingTag) {
        throw "Release or Git tag '$expectedTag' already exists. Preparation refuses an already-reserved version."
    }
}
elseif ($null -eq $existingReleaseSummary -and $null -ne $existingTag) {
    throw "Git tag '$expectedTag' exists without the matching prepared release. Refusing to reuse it."
}
elseif ($null -ne $existingTag -and
    ([string]$existingTag.object.type -ne 'commit' -or
        -not [string]::Equals(
            [string]$existingTag.object.sha,
            [string]$selectedRun.headSha,
            [StringComparison]::OrdinalIgnoreCase))) {
    throw "Git tag '$expectedTag' does not point to the prepared workflow commit."
}

$artifact = $null
if ($Prepare) {
    $artifactResponse = Invoke-GhJson -Arguments @(
        'api'
        "repos/$Repository/actions/runs/$selectedRunId/artifacts"
    )
    $matchingArtifacts = @(
        @($artifactResponse.artifacts) |
            Where-Object {
                [string]$_.name -eq $expectedArtifactName -and
                -not [bool]$_.expired
            }
    )
    if ($matchingArtifacts.Count -ne 1) {
        throw "Expected exactly one nonexpired artifact '$expectedArtifactName', but found $($matchingArtifacts.Count)."
    }
    $artifact = $matchingArtifacts[0]
}

$resolvedWorkingDirectory = $resolvedPreparedDirectory
$cleanupWorkingDirectory = $false
if ($Prepare) {
    $preparedParent = [IO.Path]::GetDirectoryName($resolvedPreparedDirectory)
    $preparedLeaf = [IO.Path]::GetFileName($resolvedPreparedDirectory)
    if ([string]::IsNullOrWhiteSpace($preparedParent) -or
        [string]::IsNullOrWhiteSpace($preparedLeaf)) {
        throw "PreparedDirectory '$resolvedPreparedDirectory' must name a child directory."
    }
    if (-not [IO.Directory]::Exists($preparedParent)) {
        [void][IO.Directory]::CreateDirectory($preparedParent)
    }
    $resolvedWorkingDirectory = Join-Path `
        $preparedParent `
        ".$preparedLeaf.preparing-$([Guid]::NewGuid().ToString('N'))"
    $resolvedWorkingDirectory = [IO.Path]::GetFullPath($resolvedWorkingDirectory)
    if (-not $resolvedWorkingDirectory.StartsWith(
            [IO.Path]::GetFullPath($preparedParent).TrimEnd('\') + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The preparation staging directory resolved outside its intended parent.'
    }
    $cleanupWorkingDirectory = $true
}

$operationFailure = $null
try {
    if ($Prepare) {
        [void][IO.Directory]::CreateDirectory($resolvedWorkingDirectory)
        Invoke-GhCommand -Arguments @(
            'run'
            'download'
            $selectedRunId.ToString()
            '--repo'
            $Repository
            '--name'
            $expectedArtifactName
            '--dir'
            $resolvedWorkingDirectory
        )
    }

    $metadataPath = Join-Path $resolvedWorkingDirectory 'release-metadata.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw 'The downloaded artifact does not contain release-metadata.json.'
    }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    foreach ($propertyName in @(
        'schemaVersion'
        'packageVersion'
        'architectures'
        'packageAsset'
        'releaseSequence'
        'tagName'
        'repository'
        'commitSha'
        'runId'
        'runNumber'
        'runAttempt'
        'artifactName'
        'workflowName'
        'generatedAtUtc'
    )) {
        Assert-MetadataProperty -Metadata $metadata -Name $propertyName
    }

    if ([int]$metadata.schemaVersion -ne 2 -or
        [string]$metadata.packageVersion -ne $expectedVersion -or
        (@($metadata.architectures) -join ',') -ne 'x64,arm64' -or
        [string]$metadata.packageAsset -ne 'PackagePilot.msixbundle' -or
        [uint64]$metadata.releaseSequence -ne $expectedSequence -or
        [string]$metadata.tagName -ne $expectedTag -or
        -not [string]::Equals(
            [string]$metadata.repository,
            $Repository,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$metadata.commitSha -ne ([string]$selectedRun.headSha).ToLowerInvariant() -or
        [uint64]$metadata.runId -ne $selectedRunId -or
        [uint64]$metadata.runNumber -ne $selectedRunNumber -or
        [uint64]$metadata.runAttempt -ne $selectedRunAttempt -or
        [string]$metadata.artifactName -ne $expectedArtifactName -or
        [string]$metadata.workflowName -ne 'Release') {
        throw 'release-metadata.json does not match the selected GitHub Actions run.'
    }

    $generatedAt = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$metadata.generatedAtUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$generatedAt)) {
        throw 'release-metadata.json contains an invalid generatedAtUtc timestamp.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packagePath = Join-Path $resolvedWorkingDirectory 'PackagePilot.msixbundle'
    $x64RuntimePath = Join-Path $resolvedWorkingDirectory 'Microsoft.WindowsAppRuntime.2.x64.msix'
    $arm64RuntimePath = Join-Path $resolvedWorkingDirectory 'Microsoft.WindowsAppRuntime.2.arm64.msix'
    $appInstallerPath = Join-Path $resolvedWorkingDirectory 'PackagePilot.appinstaller'
    foreach ($requiredPath in @($packagePath, $x64RuntimePath, $arm64RuntimePath, $appInstallerPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "The downloaded release payload is missing '$([IO.Path]::GetFileName($requiredPath))'."
        }
    }

    $packageIdentity = Get-MsixBundleIdentity -Path $packagePath
    if ($packageIdentity.Name -ne 'PackagePilot.Desktop' -or
        $packageIdentity.Publisher -ne $script:CertificateSubject -or
        $packageIdentity.Version -ne $expectedVersion -or
        ($packageIdentity.Architectures -join ',') -ne 'arm64,x64' -or
        ($Prepare -and $packageIdentity.HasSignature) -or
        ($Promote -and -not $packageIdentity.HasSignature)) {
        throw 'The Package Pilot MSIX bundle identity, architecture set, or phase-specific signature state is invalid.'
    }

    foreach ($runtime in @(
        @{ Path = $x64RuntimePath; Architecture = 'x64' }
        @{ Path = $arm64RuntimePath; Architecture = 'arm64' }
    )) {
        $runtimeIdentity = Get-MsixIdentity -Path $runtime.Path
        if ($runtimeIdentity.Name -ne 'Microsoft.WindowsAppRuntime.2' -or
            $runtimeIdentity.Publisher -ne
                'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -or
            $runtimeIdentity.Version -ne '2.2.0.0' -or
            $runtimeIdentity.ProcessorArchitecture -ne $runtime.Architecture -or
            -not $runtimeIdentity.HasSignature) {
            throw "The $($runtime.Architecture) Windows App Runtime dependency identity or signature state is invalid."
        }
        $runtimeSignature = Get-AuthenticodeSignature -FilePath $runtime.Path
        if ($runtimeSignature.Status -ne
            [System.Management.Automation.SignatureStatus]::Valid) {
            throw "The $($runtime.Architecture) Windows App Runtime signature is invalid: $($runtimeSignature.StatusMessage)"
        }
    }

    [xml]$appInstaller = Get-Content -LiteralPath $appInstallerPath -Raw
    $namespaceManager = [Xml.XmlNamespaceManager]::new($appInstaller.NameTable)
    $namespaceManager.AddNamespace('ai', 'http://schemas.microsoft.com/appx/appinstaller/2021')
    $feedRoot = $appInstaller.SelectSingleNode('/ai:AppInstaller', $namespaceManager)
    $feedPackage = $appInstaller.SelectSingleNode(
        '/ai:AppInstaller/ai:MainBundle',
        $namespaceManager)
    $feedRuntimes = @($appInstaller.SelectNodes(
        '/ai:AppInstaller/ai:Dependencies/ai:Package',
        $namespaceManager))
    $feedOnLaunch = $appInstaller.SelectSingleNode(
        '/ai:AppInstaller/ai:UpdateSettings/ai:OnLaunch',
        $namespaceManager)
    $feedBackgroundTask = $appInstaller.SelectSingleNode(
        '/ai:AppInstaller/ai:UpdateSettings/ai:AutomaticBackgroundTask',
        $namespaceManager)
    $latestBaseUri = "https://github.com/$Repository/releases/latest/download"
    if ($null -eq $feedRoot -or
        $null -eq $feedPackage -or
        $feedRoot.GetAttribute('Version') -ne $expectedVersion -or
        $feedRoot.GetAttribute('Uri') -ne "$latestBaseUri/PackagePilot.appinstaller" -or
        $feedPackage.GetAttribute('Name') -ne 'PackagePilot.Desktop' -or
        $feedPackage.GetAttribute('Publisher') -ne $script:CertificateSubject -or
        $feedPackage.GetAttribute('Version') -ne $expectedVersion -or
        $feedPackage.HasAttribute('ProcessorArchitecture') -or
        $feedPackage.GetAttribute('Uri') -ne "$latestBaseUri/PackagePilot.msixbundle" -or
        $feedRuntimes.Count -ne 2 -or
        $null -eq $feedOnLaunch -or
        $feedOnLaunch.GetAttribute('HoursBetweenUpdateChecks') -ne '24' -or
        $feedOnLaunch.GetAttribute('ShowPrompt') -ne 'false' -or
        $feedOnLaunch.GetAttribute('UpdateBlocksActivation') -ne 'false' -or
        $null -eq $feedBackgroundTask) {
        throw 'The App Installer feed does not match the selected release payload.'
    }
    $expectedRuntimeUris = @{
        x64 = "$latestBaseUri/Microsoft.WindowsAppRuntime.2.x64.msix"
        arm64 = "$latestBaseUri/Microsoft.WindowsAppRuntime.2.arm64.msix"
    }
    foreach ($feedRuntime in $feedRuntimes) {
        $architecture = $feedRuntime.GetAttribute('ProcessorArchitecture')
        if (-not $expectedRuntimeUris.ContainsKey($architecture) -or
            $feedRuntime.GetAttribute('Name') -ne 'Microsoft.WindowsAppRuntime.2' -or
            $feedRuntime.GetAttribute('Publisher') -ne
                'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -or
            $feedRuntime.GetAttribute('Version') -ne '2.2.0.0' -or
            $feedRuntime.GetAttribute('Uri') -ne $expectedRuntimeUris[$architecture]) {
            throw "The App Installer feed contains an invalid '$architecture' runtime dependency."
        }
        [void]$expectedRuntimeUris.Remove($architecture)
    }
    if ($expectedRuntimeUris.Count -ne 0) {
        throw 'The App Installer feed does not contain both architecture-specific runtime dependencies.'
    }

    $certificatePath = Join-Path $resolvedWorkingDirectory 'PackagePilot.cer'
    $checksumPath = Join-Path $resolvedWorkingDirectory 'SHA256SUMS.txt'
    if ($Prepare) {
        $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
        $kitsRoot = Join-Path $programFilesX86 'Windows Kits\10\bin'
        $signTool = Get-ChildItem -LiteralPath $kitsRoot -Recurse -File -Filter 'signtool.exe' |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -eq $signTool) {
            throw 'SignTool.exe was not found in the Windows 10/11 SDK.'
        }

        & $signTool.FullName 'sign' '/fd' 'SHA256' '/sha1' $certificate.Thumbprint '/s' 'My' '/tr' 'http://timestamp.acs.microsoft.com' '/td' 'SHA256' $packagePath
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool bundle signing failed with exit code $LASTEXITCODE."
        }
        & $signTool.FullName 'verify' '/pa' '/all' '/v' $packagePath
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool bundle verification failed with exit code $LASTEXITCODE."
        }

        Export-Certificate -Cert $certificate -FilePath $certificatePath | Out-Null
        $checksumNames = @(
            'PackagePilot.msixbundle'
            'Microsoft.WindowsAppRuntime.2.x64.msix'
            'Microsoft.WindowsAppRuntime.2.arm64.msix'
            'PackagePilot.cer'
            'PackagePilot.appinstaller'
        )
        $checksumLines = foreach ($fileName in $checksumNames) {
            $hash = Get-FileHash -LiteralPath (Join-Path $resolvedWorkingDirectory $fileName) -Algorithm SHA256
            "$($hash.Hash.ToLowerInvariant()) *$fileName"
        }
        $checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ascii
    }

    $packageSignature = Get-AuthenticodeSignature -FilePath $packagePath
    if ($packageSignature.Status -ne
            [System.Management.Automation.SignatureStatus]::Valid -or
        $packageSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "The signed Package Pilot MSIX bundle failed signer verification: $($packageSignature.StatusMessage)"
    }
    $signedIdentity = Get-MsixBundleIdentity -Path $packagePath
    if (-not $signedIdentity.HasSignature -or
        $signedIdentity.Name -ne 'PackagePilot.Desktop' -or
        $signedIdentity.Publisher -ne $script:CertificateSubject -or
        $signedIdentity.Version -ne $expectedVersion -or
        ($signedIdentity.Architectures -join ',') -ne 'arm64,x64') {
        throw 'Signing changed the bundle identity or architecture set, or did not sign the bundle and both embedded packages.'
    }
    Assert-MsixBundleEmbeddedSignatures `
        -Path $packagePath `
        -Identity $signedIdentity `
        -Certificate $certificate

    if ($Prepare) {
        $assetRecords = @($ReleaseAssetNames | ForEach-Object {
            New-PreparedFileRecord -Directory $resolvedWorkingDirectory -FileName $_
        })
        $state = [ordered]@{
            schemaVersion = 1
            repository = $Repository
            workflowName = 'Release'
            workflowDatabaseId = [uint64]$workflowDefinition.id
            runId = $selectedRunId
            runNumber = $selectedRunNumber
            runAttempt = $selectedRunAttempt
            artifactId = [uint64]$artifact.id
            artifactName = $expectedArtifactName
            commitSha = ([string]$selectedRun.headSha).ToLowerInvariant()
            releaseSequence = $expectedSequence
            tagName = $expectedTag
            packageVersion = $expectedVersion
            signerThumbprint = $certificate.Thumbprint
            preparedAtUtc = [DateTimeOffset]::UtcNow.ToString(
                'o',
                [Globalization.CultureInfo]::InvariantCulture)
            sourceMetadata = New-PreparedFileRecord `
                -Directory $resolvedWorkingDirectory `
                -FileName 'release-metadata.json'
            assets = $assetRecords
        }
        Write-SignedPreparedState `
            -Directory $resolvedWorkingDirectory `
            -State $state `
            -Certificate $certificate
        $validatedState = Read-AndVerifyPreparedState `
            -Directory $resolvedWorkingDirectory `
            -Certificate $certificate
        Assert-PreparedDirectory -Directory $resolvedWorkingDirectory -State $validatedState
        $preparedBundleHash = [string]($assetRecords |
            Where-Object { [string]$_.name -eq 'PackagePilot.msixbundle' } |
            Select-Object -First 1).sha256

        [IO.Directory]::Move($resolvedWorkingDirectory, $resolvedPreparedDirectory)
        $postMoveState = Read-AndVerifyPreparedState `
            -Directory $resolvedPreparedDirectory `
            -Certificate $certificate
        Assert-PreparedDirectory -Directory $resolvedPreparedDirectory -State $postMoveState
        $cleanupWorkingDirectory = $false
        [pscustomobject]@{
            Mode = 'Prepared'
            Repository = $Repository
            WorkflowRunId = $selectedRunId
            Release = $expectedTag
            PackageVersion = $expectedVersion
            CommitSha = [string]$selectedRun.headSha
            SignerThumbprint = $certificate.Thumbprint
            BundleSha256 = $preparedBundleHash
            PreparedDirectory = $resolvedPreparedDirectory
        } | ConvertTo-Json
        return
    }

    $preparedCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    try {
        if (-not [string]::Equals(
                $preparedCertificate.Thumbprint,
                $certificate.Thumbprint,
                [StringComparison]::OrdinalIgnoreCase) -or
            $preparedCertificate.Subject -ne $script:CertificateSubject) {
            throw 'PackagePilot.cer does not match the release signer in the prepared state.'
        }
    }
    finally {
        $preparedCertificate.Dispose()
    }

    $release = $null
    if ($null -eq $existingReleaseSummary) {
        Invoke-GhCommand -Arguments @(
            'release'
            'create'
            $expectedTag
            '--repo'
            $Repository
            '--target'
            ([string]$selectedRun.headSha)
            '--title'
            "Package Pilot 1.0.$expectedSequence"
            '--generate-notes'
            '--draft'
            '--latest=false'
        )
        $release = Get-ReleaseView -TagName $expectedTag
    }
    else {
        $release = Get-ReleaseView -TagName $expectedTag
    }

    Assert-GitHubReleaseBinding `
        -Release $release `
        -TagName $expectedTag `
        -CommitSha ([string]$selectedRun.headSha)

    $missingAssets = @(Assert-GitHubReleaseAssets `
        -Release $release `
        -State $preparedState `
        -AllowMissing)
    if (-not [bool]$release.isDraft -and $missingAssets.Count -ne 0) {
        throw "Published release '$expectedTag' is incomplete and cannot be repaired in place."
    }
    foreach ($assetName in $missingAssets) {
        Invoke-GhCommand -Arguments @(
            'release'
            'upload'
            $expectedTag
            (Join-Path $resolvedPreparedDirectory $assetName)
            '--repo'
            $Repository
        )
    }

    $release = Get-ReleaseView -TagName $expectedTag
    Assert-GitHubReleaseBinding `
        -Release $release `
        -TagName $expectedTag `
        -CommitSha ([string]$selectedRun.headSha)
    [void](Assert-GitHubReleaseAssets -Release $release -State $preparedState)
    if ([bool]$release.isDraft) {
        Assert-PromotionPreconditionsStillHold `
            -WorkflowDatabaseId ([uint64]$workflowDefinition.id) `
            -WorkflowRunId $selectedRunId `
            -RunNumber $selectedRunNumber `
            -RunAttempt $selectedRunAttempt `
            -CommitSha ([string]$selectedRun.headSha) `
            -ReleaseSequence $expectedSequence `
            -TagName $expectedTag
        $promotionFailure = $null
        try {
            Invoke-GhCommand -Arguments @(
                'release'
                'edit'
                $expectedTag
                '--repo'
                $Repository
                '--draft=false'
                '--latest'
            )
        }
        catch {
            $promotionFailure = $_.Exception.Message
        }
        $release = Get-ReleaseView -TagName $expectedTag
        if ([bool]$release.isDraft) {
            throw "Release promotion did not complete and the release remains a recoverable draft. $promotionFailure"
        }
    }

    Assert-GitHubReleaseBinding `
        -Release $release `
        -TagName $expectedTag `
        -CommitSha ([string]$selectedRun.headSha)
    [void](Assert-GitHubReleaseAssets -Release $release -State $preparedState)
    if ([bool]$release.isDraft -or [bool]$release.isPrerelease) {
        throw "Release '$expectedTag' was not published as a stable release."
    }

    $latestRelease = Invoke-GhJson -Arguments @(
        'release'
        'view'
        '--repo'
        $Repository
        '--json'
        'tagName'
    )
    if ([string]$latestRelease.tagName -ne $expectedTag) {
        Assert-PromotionPreconditionsStillHold `
            -WorkflowDatabaseId ([uint64]$workflowDefinition.id) `
            -WorkflowRunId $selectedRunId `
            -RunNumber $selectedRunNumber `
            -RunAttempt $selectedRunAttempt `
            -CommitSha ([string]$selectedRun.headSha) `
            -ReleaseSequence $expectedSequence `
            -TagName $expectedTag
        Invoke-GhCommand -Arguments @(
            'release'
            'edit'
            $expectedTag
            '--repo'
            $Repository
            '--latest'
        )
        $latestRelease = Invoke-GhJson -Arguments @(
            'release'
            'view'
            '--repo'
            $Repository
            '--json'
            'tagName'
        )
    }
    if ([string]$latestRelease.tagName -ne $expectedTag) {
        throw "The published release '$expectedTag' was not selected as the latest release."
    }

    $createdTag = Get-ExactTagReference -TagName $expectedTag
    if ($null -eq $createdTag -or
        [string]$createdTag.object.type -ne 'commit' -or
        -not [string]::Equals(
            [string]$createdTag.object.sha,
            [string]$selectedRun.headSha,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The published tag '$expectedTag' does not point to the verified workflow commit."
    }

    [pscustomobject]@{
        Mode = 'Promoted'
        Repository = $Repository
        WorkflowRunId = $selectedRunId
        Release = $expectedTag
        PackageVersion = $expectedVersion
        CommitSha = [string]$selectedRun.headSha
        SignerThumbprint = $certificate.Thumbprint
        PreparedDirectory = $resolvedPreparedDirectory
        ReleaseUrl = [string]$release.url
    } | ConvertTo-Json
}
catch {
    $operationFailure = $_
    throw
}
finally {
    if ($cleanupWorkingDirectory -and (Test-Path -LiteralPath $resolvedWorkingDirectory)) {
        try {
            $cleanupPath = [IO.Path]::GetFullPath($resolvedWorkingDirectory)
            $cleanupParent = [IO.Path]::GetFullPath(
                [IO.Path]::GetDirectoryName($resolvedPreparedDirectory)).TrimEnd('\')
            if (-not $cleanupPath.StartsWith(
                    $cleanupParent + '\',
                    [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($cleanupPath) -notmatch '^\..+\.preparing-[0-9a-f]{32}$') {
                throw "Refusing to clean unsafe temporary path '$cleanupPath'."
            }
            Remove-Item -LiteralPath $cleanupPath -Recurse -Force
        }
        catch {
            if ($null -ne $operationFailure) {
                Write-Warning "Preparation failed and staging cleanup also failed: $($_.Exception.Message)"
            }
            else {
                throw
            }
        }
    }
}
