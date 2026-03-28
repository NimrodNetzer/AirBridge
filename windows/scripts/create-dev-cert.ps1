<#
.SYNOPSIS
    Creates a self-signed code-signing certificate for AirBridge developer sideloading.

.DESCRIPTION
    Run this script ONCE per developer machine.  It will:
      1. Create a self-signed code-signing certificate with subject "CN=AirBridge Dev".
      2. Export the certificate thumbprint so you can pass it to build-msix.ps1.
      3. Export the certificate as a .pfx file (password-protected).
      4. Install the certificate into the Trusted Root and Trusted People stores
         (both are required to sideload an MSIX package signed by a self-signed cert).

.PARAMETER PfxPassword
    Password for the exported .pfx file.  Prompted interactively if not supplied.

.PARAMETER PfxOutputPath
    Where to write the .pfx file.  Defaults to the script directory.
    NOTE: .pfx files are listed in .gitignore — never commit them.

.EXAMPLE
    .\create-dev-cert.ps1
    .\create-dev-cert.ps1 -PfxPassword (ConvertTo-SecureString "s3cr3t" -AsPlainText -Force)
#>

[CmdletBinding()]
param (
    [SecureString] $PfxPassword,
    [string]       $PfxOutputPath = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 1. Ask for a PFX password if not supplied
# ---------------------------------------------------------------------------
if (-not $PfxPassword) {
    $PfxPassword = Read-Host -Prompt "Enter a password for the exported .pfx file" -AsSecureString
}

# ---------------------------------------------------------------------------
# 2. Create the certificate
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Creating self-signed code-signing certificate..." -ForegroundColor Cyan

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=AirBridge Dev" `
    -KeyUsage DigitalSignature `
    -FriendlyName "AirBridge Dev Cert" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @(
        "2.5.29.37={text}1.3.6.1.5.5.7.3.3",   # Extended Key Usage: Code Signing
        "2.5.29.19={text}"                         # Basic Constraints: end-entity
    )

$thumbprint = $cert.Thumbprint
Write-Host "  Certificate created.  Thumbprint: $thumbprint" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 3. Export as .pfx
# ---------------------------------------------------------------------------
$pfxPath = Join-Path $PfxOutputPath "AirBridgeDev.pfx"

Export-PfxCertificate `
    -Cert "Cert:\CurrentUser\My\$thumbprint" `
    -FilePath $pfxPath `
    -Password $PfxPassword | Out-Null

Write-Host "  Certificate exported to: $pfxPath" -ForegroundColor Green
Write-Host "  Keep this file safe — do NOT commit it to git." -ForegroundColor Yellow

# ---------------------------------------------------------------------------
# 4. Install into Trusted Root and Trusted People (requires elevation for
#    LocalMachine stores; CurrentUser stores work without elevation)
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Installing certificate into trust stores..." -ForegroundColor Cyan

# Trusted Root — CurrentUser (no elevation required)
$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store(
    [System.Security.Cryptography.X509Certificates.StoreName]::Root,
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
)
$rootStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$rootStore.Add($cert)
$rootStore.Close()
Write-Host "  Added to CurrentUser\Root (Trusted Root Certification Authorities)" -ForegroundColor Green

# Trusted People — CurrentUser (no elevation required)
$peopleStore = New-Object System.Security.Cryptography.X509Certificates.X509Store(
    [System.Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser
)
$peopleStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$peopleStore.Add($cert)
$peopleStore.Close()
Write-Host "  Added to CurrentUser\TrustedPeople (Trusted People)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 5. Print next steps
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "=======================================================" -ForegroundColor White
Write-Host " Setup complete!  Next steps:" -ForegroundColor White
Write-Host "=======================================================" -ForegroundColor White
Write-Host ""
Write-Host "  Build the MSIX:" -ForegroundColor Cyan
Write-Host "    .\build-msix.ps1 -CertThumbprint $thumbprint" -ForegroundColor White
Write-Host ""
Write-Host "  Or copy the thumbprint now for later use:" -ForegroundColor Cyan
Write-Host "    $thumbprint" -ForegroundColor Yellow
Write-Host ""
Write-Host "  See windows/MSIX_PACKAGING.md for full instructions." -ForegroundColor Gray
Write-Host ""
