# AirBridge — MSIX Packaging Guide

This document explains how to build and sideload the AirBridge Windows app as an MSIX package on any Windows 11 machine, without a Microsoft Store account.

---

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| Windows 11 (or Windows 10 1903+) | Sideloading is enabled by default on Windows 11 |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Required to build |
| [Windows App SDK 1.5](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) | Install the MSIX runtime redistributable |
| Visual Studio 2022 (optional) | Not required for the scripts, but useful for debugging |
| PowerShell 5.1 or PowerShell 7+ | Both work; run as a regular user (not elevated) for cert creation |

---

## How packaging works

The app project (`AirBridge.App.csproj`) already has:

```xml
<EnableMsixTooling>true</EnableMsixTooling>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
```

This means the app project produces an MSIX directly via `dotnet msbuild /t:Publish` — no separate Windows Application Packaging Project (`.wapproj`) is needed.

The package identity in `Package.appxmanifest` uses `Publisher="CN=AirBridge Dev"`, which matches the subject of the developer certificate created in Step 1 below.

---

## Step 1 — Create the developer signing certificate (once per machine)

Run this from the `windows/scripts/` directory:

```powershell
.\create-dev-cert.ps1
```

The script will:
1. Create a self-signed code-signing certificate (`CN=AirBridge Dev`) in your user certificate store.
2. Export it as `AirBridgeDev.pfx` in the scripts directory (do **not** commit this file — it is already in `.gitignore`).
3. Install it into the **Trusted Root** and **Trusted People** CurrentUser stores — both are required for Windows to accept a sideloaded package signed by a self-signed cert.
4. Print the certificate thumbprint you need for Step 2.

> **Security note:** The certificate is stored in your Windows user certificate store and is valid for one year by default (`New-SelfSignedCertificate` default). It is not trusted by any other machine.

---

## Step 2 — Build the MSIX package

```powershell
.\build-msix.ps1 -CertThumbprint <thumbprint printed in Step 1>
```

Example:

```powershell
.\build-msix.ps1 -CertThumbprint A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2
```

The script runs `dotnet msbuild /t:Publish` against `AirBridge.App.csproj` with the `msix-sideload` publish profile and outputs the package to:

```
windows/output/msix/
```

The `windows/output/` directory is listed in `.gitignore` and is never committed.

---

## Step 3 — Enable sideloading on the target machine (Windows 11)

Sideloading is on by default in Windows 11, but if it is disabled:

1. Open **Settings** → **System** → **For developers**.
2. Enable **Install apps from any source, including loose files** (Developer Mode) — or at minimum enable **Sideload apps**.

For Windows 10, go to **Settings** → **Update & Security** → **For developers**.

---

## Step 4 — Install the MSIX

**Option A — PowerShell (recommended):**

```powershell
Add-AppxPackage -Path "windows\output\msix\AirBridge.msix"
```

**Option B — Double-click:**

Open `windows\output\msix\` in Explorer and double-click the `.msix` file.  The App Installer UI will walk you through installation.

> **Note:** If you see "The publisher could not be verified", the certificate was not installed in the Trusted People store.  Re-run `create-dev-cert.ps1`.

---

## Uninstall

**Settings** → **Apps** → search for **AirBridge** → **Uninstall**.

Or from PowerShell:

```powershell
Get-AppxPackage -Name AirBridge.App | Remove-AppxPackage
```

---

## IddCx Virtual Display Driver

The tablet second-monitor feature requires the Indirect Display Driver (`AirBridge.IddDriver`), which is a kernel-mode component and must be installed separately — it cannot be bundled in the app MSIX.

See `windows/AirBridge.IddDriver/README.md` for driver installation instructions.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| `0x80073CF0` — publisher mismatch | The cert subject does not match `CN=AirBridge Dev` in the manifest | Re-run `create-dev-cert.ps1` and use the new thumbprint |
| `0x800B0109` — untrusted publisher | Cert not in Trusted Root or Trusted People | Re-run `create-dev-cert.ps1`; it installs to both stores |
| `Build failed: certificate not found` | Wrong thumbprint passed | Run `Get-ChildItem Cert:\CurrentUser\My` to list certs and find the correct thumbprint |
| App installs but crashes on launch | Missing Windows App SDK runtime | Install the [Windows App SDK 1.5 runtime redistributable](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) |

---

## Automated / CI builds

For unattended builds (e.g. GitHub Actions), import the `.pfx` from a secret:

```powershell
$pfxBytes = [Convert]::FromBase64String($env:AIRBRIDGE_PFX_BASE64)
$pfxPath  = "$env:TEMP\AirBridgeDev.pfx"
[IO.File]::WriteAllBytes($pfxPath, $pfxBytes)

$cert = Import-PfxCertificate -FilePath $pfxPath `
    -CertStoreLocation Cert:\CurrentUser\My `
    -Password (ConvertTo-SecureString $env:AIRBRIDGE_PFX_PASSWORD -AsPlainText -Force)

.\build-msix.ps1 -CertThumbprint $cert.Thumbprint
```

Store `AIRBRIDGE_PFX_BASE64` and `AIRBRIDGE_PFX_PASSWORD` as encrypted repository secrets — never in source control.
