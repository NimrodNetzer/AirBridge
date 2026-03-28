<#
.SYNOPSIS
    Builds a self-signed MSIX package for the AirBridge Windows app.

.DESCRIPTION
    Runs `dotnet publish` against AirBridge.App using the msix-sideload publish
    profile.  Produces a single MSIX file in windows/output/msix/.

    Before running this script for the first time, create a developer signing
    certificate with windows/scripts/create-dev-cert.ps1.

.PARAMETER CertThumbprint
    Thumbprint of the code-signing certificate to use.
    If omitted the build will fail at the signing step — always supply this.

.PARAMETER Configuration
    Build configuration.  Defaults to Release.

.EXAMPLE
    .\build-msix.ps1 -CertThumbprint A1B2C3D4E5F6...
    .\build-msix.ps1 -CertThumbprint A1B2C3D4E5F6... -Configuration Debug
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string] $CertThumbprint,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------------
$scriptDir   = $PSScriptRoot                                           # windows/scripts/
$windowsDir  = Split-Path $scriptDir -Parent                          # windows/
$solutionDir = Split-Path $windowsDir -Parent                         # repo root
$appCsproj   = Join-Path $windowsDir "AirBridge.App\AirBridge.App.csproj"
$outputDir   = Join-Path $windowsDir "output\msix"

if (-not (Test-Path $appCsproj)) {
    Write-Error "Cannot find app project at: $appCsproj"
    exit 1
}

# ---------------------------------------------------------------------------
# Clean output directory
# ---------------------------------------------------------------------------
if (Test-Path $outputDir) {
    Write-Host "Cleaning output directory..." -ForegroundColor Cyan
    Remove-Item $outputDir -Recurse -Force
}
New-Item $outputDir -ItemType Directory -Force | Out-Null

# ---------------------------------------------------------------------------
# Build + publish
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Building MSIX package..." -ForegroundColor Cyan
Write-Host "  Project    : $appCsproj"
Write-Host "  Config     : $Configuration"
Write-Host "  Thumbprint : $CertThumbprint"
Write-Host "  Output     : $outputDir"
Write-Host ""

$msbuildArgs = @(
    $appCsproj
    "/p:Configuration=$Configuration"
    "/p:Platform=x64"
    "/p:RuntimeIdentifier=win-x64"
    "/p:PublishProfile=msix-sideload"
    "/p:AppxPackageSigningEnabled=true"
    "/p:PackageCertificateThumbprint=$CertThumbprint"
    "/p:PublishDir=$outputDir\"
    "/p:AppxBundle=Never"
    "/p:UapAppxPackageBuildMode=SideloadOnly"
    "/t:Publish"
    "/restore"
    "/nologo"
    "/v:minimal"
)

& dotnet msbuild @msbuildArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# ---------------------------------------------------------------------------
# Locate the generated .msix file
# ---------------------------------------------------------------------------
$msixFile = Get-ChildItem -Path $outputDir -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

Write-Host ""
if ($msixFile) {
    Write-Host "=======================================================" -ForegroundColor White
    Write-Host " Build succeeded!" -ForegroundColor Green
    Write-Host "=======================================================" -ForegroundColor White
    Write-Host ""
    Write-Host "  MSIX package: $($msixFile.FullName)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  To install (run in an elevated PowerShell):" -ForegroundColor Cyan
    Write-Host "    Add-AppxPackage -Path `"$($msixFile.FullName)`"" -ForegroundColor White
    Write-Host ""
    Write-Host "  Or double-click the .msix file in Explorer." -ForegroundColor Gray
} else {
    Write-Host "Build completed but no .msix file was found in $outputDir" -ForegroundColor Yellow
    Write-Host "Check the build output above for errors or unexpected output paths." -ForegroundColor Yellow
}
Write-Host ""
