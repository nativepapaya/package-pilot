#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$CertificatePath = (Join-Path $PSScriptRoot 'PackagePilot.ManualRelease.cer')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Subject = 'CN=PackagePilot.Dev'
$FriendlyName = 'Package Pilot manual release signing'

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
            $exportPolicy = $privateKey.Key.ExportPolicy
            return $exportPolicy -eq [System.Security.Cryptography.CngExportPolicies]::None
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

function Assert-ReleaseCertificate {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if ($Certificate.Subject -ne $Subject -or $Certificate.Issuer -ne $Subject) {
        throw "The release certificate must be self-signed with subject '$Subject'."
    }
    if (-not $Certificate.HasPrivateKey) {
        throw 'The release certificate does not have a private key.'
    }
    if ($Certificate.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1' -or
        $Certificate.PublicKey.Key.KeySize -lt 3072) {
        throw 'The release certificate must use an RSA private key of at least 3072 bits.'
    }
    if ($Certificate.NotBefore -gt (Get-Date) -or $Certificate.NotAfter -le (Get-Date).AddDays(30)) {
        throw 'The release certificate is not yet valid, is expired, or expires within 30 days.'
    }

    $enhancedKeyUsages = @(
        $Certificate.EnhancedKeyUsageList |
            ForEach-Object { $_.ObjectId.Value }
    )
    if ($enhancedKeyUsages.Count -ne 1 -or
        $enhancedKeyUsages[0] -ne '1.3.6.1.5.5.7.3.3') {
        throw 'The release certificate must be restricted to the code-signing enhanced key usage.'
    }
    $keyUsage = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.15' } |
        Select-Object -First 1
    if ($null -eq $keyUsage -or
        $keyUsage.KeyUsages -ne
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) {
        throw 'The release certificate is not restricted to digital-signature usage.'
    }
    $basicConstraints = $Certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
        Select-Object -First 1
    if ($null -eq $basicConstraints -or $basicConstraints.CertificateAuthority) {
        throw 'The release certificate must be an end-entity certificate, not a certificate authority.'
    }
    if (-not (Test-NonExportablePrivateKey -Certificate $Certificate)) {
        throw 'The release certificate private key is exportable or its non-exportable policy cannot be verified.'
    }
}

$matches = @(
    Get-ChildItem 'Cert:\CurrentUser\My' |
        Where-Object {
            $_.FriendlyName -eq $FriendlyName -and
            $_.Subject -eq $Subject
        }
)
if ($matches.Count -gt 1) {
    throw "Multiple '$FriendlyName' certificates exist. Remove the obsolete copies before continuing."
}

$created = $false
$certificate = $null
$resolvedCertificatePath = $null
$trustedCertificatePath = $null
$trustedCertificateWasPresent = $false

try {
    if ($matches.Count -eq 1) {
        $certificate = $matches[0]
    }
    else {
        $now = Get-Date
        $certificateParameters = @{
            Type = 'Custom'
            Subject = $Subject
            FriendlyName = $FriendlyName
            CertStoreLocation = 'Cert:\CurrentUser\My'
            KeyAlgorithm = 'RSA'
            KeyLength = 3072
            HashAlgorithm = 'SHA256'
            KeyUsage = 'DigitalSignature'
            KeyExportPolicy = 'NonExportable'
            NotBefore = $now.AddMinutes(-5)
            NotAfter = $now.AddYears(2)
            TextExtension = @(
                '2.5.29.19={text}'
                '2.5.29.37={text}1.3.6.1.5.5.7.3.3'
            )
        }
        $certificate = New-SelfSignedCertificate @certificateParameters
        $created = $true
    }

    Assert-ReleaseCertificate -Certificate $certificate

    $resolvedCertificatePath =
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CertificatePath)
    if ([IO.Path]::GetExtension($resolvedCertificatePath) -ine '.cer') {
        throw "CertificatePath must use the .cer extension: '$resolvedCertificatePath'."
    }

    $certificateDirectory = [IO.Path]::GetDirectoryName($resolvedCertificatePath)
    if (-not [IO.Directory]::Exists($certificateDirectory)) {
        [void][IO.Directory]::CreateDirectory($certificateDirectory)
    }

    Export-Certificate -Cert $certificate -FilePath $resolvedCertificatePath -Force | Out-Null

    $trustedCertificatePath = "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)"
    $trustedCertificateWasPresent = Test-Path -LiteralPath $trustedCertificatePath
    if (-not $trustedCertificateWasPresent) {
        Import-Certificate `
            -FilePath $resolvedCertificatePath `
            -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' |
            Out-Null
    }

    $trustedCertificate = Get-Item -LiteralPath $trustedCertificatePath
    if ($trustedCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw 'The public release certificate was not trusted in LocalMachine\TrustedPeople.'
    }

    [pscustomobject]@{
        Subject = $certificate.Subject
        FriendlyName = $certificate.FriendlyName
        Thumbprint = $certificate.Thumbprint
        KeyAlgorithm = $certificate.PublicKey.Oid.FriendlyName
        KeyBits = $certificate.PublicKey.Key.KeySize
        NonExportable = $true
        Created = $created
        NotBefore = $certificate.NotBefore.ToString('o')
        NotAfter = $certificate.NotAfter.ToString('o')
        PublicCertificatePath = (Resolve-Path -LiteralPath $resolvedCertificatePath).Path
        TrustedForLocalMachine = $true
    } | ConvertTo-Json
}
catch {
    if (-not $trustedCertificateWasPresent -and
        -not [string]::IsNullOrWhiteSpace($trustedCertificatePath) -and
        (Test-Path -LiteralPath $trustedCertificatePath)) {
        Remove-Item -LiteralPath $trustedCertificatePath -Force -ErrorAction SilentlyContinue
    }
    if ($created -and $null -ne $certificate) {
        Remove-Item `
            -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" `
            -Force `
            -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace($resolvedCertificatePath) -and
            (Test-Path -LiteralPath $resolvedCertificatePath -PathType Leaf)) {
            Remove-Item -LiteralPath $resolvedCertificatePath -Force -ErrorAction SilentlyContinue
        }
    }
    throw
}
