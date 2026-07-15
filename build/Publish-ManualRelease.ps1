#Requires -Version 5.1

[CmdletBinding()]
param(
    [uint64]$RunId = 0,

    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:CertificateSubject = 'CN=PackagePilot.Dev'
$script:CertificateFriendlyName = 'Package Pilot manual release signing'
$script:GhExecutable = $null
$Repository = 'nativepapaya/package-pilot'

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

function Get-MsixIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

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

        $identity = $manifest.Package.Identity
        return [pscustomobject]@{
            Name = [string]$identity.Name
            Publisher = [string]$identity.Publisher
            Version = [string]$identity.Version
            ProcessorArchitecture = [string]$identity.ProcessorArchitecture
            HasSignature = $null -ne $archive.GetEntry('AppxSignature.p7x')
        }
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
    $result = Invoke-GhJson -Arguments @(
        'release'
        'list'
        '--repo'
        $Repository
        '--limit'
        '1000'
        '--json'
        'tagName,isDraft,isPrerelease'
    )
    return @($result | Where-Object { $null -ne $_ })
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
        [uint64]$MinimumSequenceExclusive
    )

    if ($RunId -gt 0) {
        $run = Invoke-GhJson -Arguments @(
            'run'
            'view'
            $RunId.ToString()
            '--repo'
            $Repository
            '--json'
            'attempt,conclusion,databaseId,event,headBranch,headSha,number,status,workflowDatabaseId,workflowName'
        )
        return $run
    }

    $runs = @(
        (Invoke-GhJson -Arguments @(
            'run'
            'list'
            '--repo'
            $Repository
            '--workflow'
            'release.yml'
            '--branch'
            'main'
            '--status'
            'success'
            '--limit'
            '100'
            '--json'
            'attempt,conclusion,databaseId,event,headBranch,headSha,number,status,workflowDatabaseId,workflowName'
        )) | Where-Object { $null -ne $_ }
    )

    foreach ($candidate in $runs | Sort-Object { [uint64]$_.number }) {
        [uint64]$sequence = [uint64]$candidate.number + 4
        if ($sequence -gt $MinimumSequenceExclusive) {
            return $candidate
        }
    }

    throw 'No unpublished successful main-branch Release workflow run was found.'
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

$existingReleases = @(Get-ExistingReleases)
[uint64]$releaseHighWaterMark = Get-ReleaseHighWaterMark -Releases $existingReleases
$selectedRun = Get-SelectedRun -MinimumSequenceExclusive $releaseHighWaterMark
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
if ($expectedSequence -le $releaseHighWaterMark) {
    throw "Release '$expectedTag' is not newer than the existing v1.0.$releaseHighWaterMark high-water mark."
}
$existingRelease = $existingReleases |
    Where-Object { [string]$_.tagName -eq $expectedTag } |
    Select-Object -First 1
if ($null -ne $existingRelease) {
    throw "Release '$expectedTag' already exists. Existing releases are never overwritten."
}
if ($null -ne (Get-ExactTagReference -TagName $expectedTag)) {
    throw "Git tag '$expectedTag' already exists. Refusing to reuse an existing tag."
}

$artifactResponse = Invoke-GhJson -Arguments @(
    'api'
    "repos/$Repository/actions/runs/$selectedRunId/artifacts"
)
$artifact = @($artifactResponse.artifacts) |
    Where-Object {
        [string]$_.name -eq $expectedArtifactName -and
        -not [bool]$_.expired
    } |
    Sort-Object created_at -Descending |
    Select-Object -First 1
if ($null -eq $artifact) {
    throw "The nonexpired artifact '$expectedArtifactName' was not found. Rerun the Release workflow if its 30-day retention expired."
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$workingDirectory = Join-Path $temporaryRoot "PackagePilot-ManualRelease-$([Guid]::NewGuid().ToString('N'))"
$resolvedWorkingDirectory = [IO.Path]::GetFullPath($workingDirectory)
if (-not $resolvedWorkingDirectory.StartsWith(
        $temporaryRoot + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The release working directory resolved outside the system temporary directory.'
}

try {
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
        $packageIdentity.HasSignature) {
        throw 'The unsigned Package Pilot MSIX bundle identity, architecture set, or signature state is invalid.'
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

    $certificate = Get-ReleaseCertificate
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

    $packageSignature = Get-AuthenticodeSignature -FilePath $packagePath
    if ($packageSignature.Status -ne
            [System.Management.Automation.SignatureStatus]::Valid -or
        $packageSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "The signed Package Pilot MSIX bundle failed signer verification: $($packageSignature.StatusMessage)"
    }
    & $signTool.FullName 'verify' '/pa' '/all' '/v' $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool bundle verification failed with exit code $LASTEXITCODE."
    }

    $signedIdentity = Get-MsixBundleIdentity -Path $packagePath
    if (-not $signedIdentity.HasSignature -or
        $signedIdentity.Name -ne 'PackagePilot.Desktop' -or
        $signedIdentity.Publisher -ne $script:CertificateSubject -or
        $signedIdentity.Version -ne $expectedVersion -or
        ($signedIdentity.Architectures -join ',') -ne 'arm64,x64') {
        throw 'Signing changed the bundle identity or architecture set, or did not add AppxSignature.p7x.'
    }

    $certificatePath = Join-Path $resolvedWorkingDirectory 'PackagePilot.cer'
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null

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
    $checksumPath = Join-Path $resolvedWorkingDirectory 'SHA256SUMS.txt'
    $checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ascii

    $assets = @(
        $packagePath
        $x64RuntimePath
        $arm64RuntimePath
        $certificatePath
        $appInstallerPath
        $checksumPath
    )
    $releaseArguments = @('release', 'create', $expectedTag) +
        $assets +
        @(
            '--repo'
            $Repository
            '--target'
            ([string]$selectedRun.headSha)
            '--title'
            "Package Pilot 1.0.$expectedSequence"
            '--generate-notes'
            '--latest'
        )
    Invoke-GhCommand -Arguments $releaseArguments

    $publishedRelease = Invoke-GhJson -Arguments @(
        'release'
        'view'
        $expectedTag
        '--repo'
        $Repository
        '--json'
        'assets,isDraft,isPrerelease,tagName,targetCommitish,url'
    )
    $expectedAssetNames = @(
        'Microsoft.WindowsAppRuntime.2.arm64.msix'
        'Microsoft.WindowsAppRuntime.2.x64.msix'
        'PackagePilot.appinstaller'
        'PackagePilot.cer'
        'PackagePilot.msixbundle'
        'SHA256SUMS.txt'
    ) | Sort-Object
    $publishedAssetNames = @(
        $publishedRelease.assets |
            ForEach-Object { [string]$_.name }
    ) | Sort-Object
    if ([string]$publishedRelease.tagName -ne $expectedTag -or
        [bool]$publishedRelease.isDraft -or
        [bool]$publishedRelease.isPrerelease -or
        -not [string]::Equals(
            [string]$publishedRelease.targetCommitish,
            [string]$selectedRun.headSha,
            [StringComparison]::OrdinalIgnoreCase) -or
        ($publishedAssetNames -join "`n") -ne ($expectedAssetNames -join "`n")) {
        throw "The published release '$expectedTag' failed post-publication verification."
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
        Repository = $Repository
        WorkflowRunId = $selectedRunId
        Release = $expectedTag
        PackageVersion = $expectedVersion
        CommitSha = [string]$selectedRun.headSha
        SignerThumbprint = $certificate.Thumbprint
        ReleaseUrl = [string]$publishedRelease.url
    } | ConvertTo-Json
}
finally {
    if (Test-Path -LiteralPath $resolvedWorkingDirectory) {
        $cleanupPath = [IO.Path]::GetFullPath($resolvedWorkingDirectory)
        if (-not $cleanupPath.StartsWith(
                $temporaryRoot + '\',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean unsafe temporary path '$cleanupPath'."
        }
        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
    }
}
