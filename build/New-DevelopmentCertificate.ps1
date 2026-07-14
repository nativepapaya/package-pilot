param(
    [string]$Subject = 'CN=PackagePilot.Dev',
    [string]$CertificatePath = (Join-Path $PSScriptRoot 'PackagePilot.Dev.cer')
)

$ErrorActionPreference = 'Stop'
$now = Get-Date
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq $Subject -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt $now.AddMonths(1) -and
        $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3'
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $certificate) {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Subject `
        -FriendlyName 'Package Pilot local development signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy NonExportable `
        -NotAfter $now.AddYears(2) `
        -TextExtension @(
            '2.5.29.19={text}',
            '2.5.29.37={text}1.3.6.1.5.5.7.3.3'
        )
}

Export-Certificate -Cert $certificate -FilePath $CertificatePath -Force | Out-Null
$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object Thumbprint -eq $certificate.Thumbprint |
    Select-Object -First 1
if ($null -eq $trusted) {
    # App Installer validates self-signed MSIX certificates against the computer store.
    # This requires an elevated PowerShell process and is intended for local testing only.
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
}

[pscustomobject]@{
    Subject = $certificate.Subject
    Thumbprint = $certificate.Thumbprint
    NotAfter = $certificate.NotAfter.ToString('o')
    CertificatePath = (Resolve-Path $CertificatePath).Path
    TrustedForLocalMachine = $true
} | ConvertTo-Json
