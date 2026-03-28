# AirBridge.IddDriver — IddCx Virtual Display Driver

## Overview

This is a **UMDF2** (User-Mode Driver Framework v2) Indirect Display Driver (IddCx)
that creates one virtual 2560x1600 monitor on a Windows PC. When the OS assigns a
swap chain to the monitor (i.e., an app window is displayed on it), the driver:

1. Captures each frame from the IddCx swap chain via `IddCxSwapChainAcquireNextFrame`
2. Copies the D3D11 texture to a CPU-accessible staging buffer
3. H.264-encodes the raw BGRA pixels via the Windows Media Foundation MFT
4. Writes length-prefixed NAL units to the named pipe `\\.\pipe\AirBridgeIdd`

The C# `TabletDisplaySession` reads from that pipe and sends each NAL unit as a
`MirrorFrameMessage (0x21)` over the existing TLS channel to the Android tablet.

---

## Named Pipe Protocol

The driver creates the server end of `\\.\pipe\AirBridgeIdd` after adapter
initialisation. `TabletDisplaySession` opens it as a client.

**Packet format (binary, big-endian):**

```
[4 bytes — uint32 big-endian — NAL length N]
[N bytes — raw H.264 NAL unit]
```

Each NAL unit is wrapped in a `MirrorFrameMessage` and forwarded over TLS to the
Android tablet, which decodes and renders it full-screen.

---

## Build Prerequisites

| Requirement | Notes |
|-------------|-------|
| Windows 11 (22H2 or later) | Test machine must have Secure Boot **disabled** for test-signing |
| Visual Studio 2022 (17.x) | "Desktop development with C++" workload |
| Windows SDK 10.0.22621 | Installed by VS installer |
| Windows Driver Kit (WDK) 10.0.22621 | Must match the Windows SDK version exactly |
| WDK VS Integration (WDK.vsix) | Installed from the WDK download page — provides the `WindowsUserModeDriver10.0` toolset |
| Spectre-mitigated ATL/MFC libs | Optional VS component: "MSVC v143 - VS 2022 C++ x64/x86 Spectre-mitigated libs" |

Download the WDK: https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk

The WDK **must** be installed alongside the matching Windows SDK version. A version
mismatch causes the `WindowsUserModeDriver10.0` toolset to fail to resolve headers.

---

## Build Steps

1. Open `windows/AirBridge.sln` in Visual Studio 2022.
2. Ensure the WDK VS integration (WDK.vsix) is installed — the
   `AirBridge.IddDriver` project uses the `WindowsUserModeDriver10.0` platform toolset.
3. Select **Debug | x64** configuration.
4. Right-click `AirBridge.IddDriver` in Solution Explorer and choose **Build**.
5. Output artifacts (in `x64/Debug/`):
   - `AirBridge.IddDriver.dll` — the UMDF2 driver DLL
   - `AirBridge.IddDriver.inf` — the INF installation file
   - `AirBridge.IddDriver.inf.cat` — unsigned catalog (produced by the WDK build)

> **Note:** The WDK build system runs `inf2cat` automatically when the project
> targets a driver configuration. If `AirBridge.IddDriver.inf.cat` is not produced,
> run `inf2cat` manually (see signing steps below).

---

## Installation on a Developer / Test Machine

### Step 1 — Disable Secure Boot (one-time, in UEFI firmware)

Test-signed drivers cannot load on machines with Secure Boot enabled.

1. Reboot into UEFI firmware settings (typically by holding F2 / Delete during POST).
2. Navigate to the Security tab and set **Secure Boot** to **Disabled**.
3. Save and reboot into Windows.

### Step 2 — Enable test-signing mode

Open an **elevated** Command Prompt (Run as Administrator):

```cmd
bcdedit /set testsigning on
```

Reboot. A "Test Mode" watermark appears in the bottom-right corner of the desktop
— this is expected and confirms test-signing is active.

### Step 3 — Create a self-signed test certificate

Run the following in an elevated **Developer Command Prompt for VS 2022**
(so `makecert.exe` and `signtool.exe` are on `PATH`):

```cmd
cd /d x64\Debug

makecert -r -pe -ss PrivateCertStore -n "CN=AirBridgeTestCert" AirBridgeTestCert.cer
```

- `-r` — self-signed root certificate
- `-pe` — private key exportable
- `-ss PrivateCertStore` — store in the current user's private certificate store
- `-n "CN=AirBridgeTestCert"` — certificate subject name

Trust the certificate for code signing by adding it to the local machine stores:

```cmd
certutil -addstore Root AirBridgeTestCert.cer
certutil -addstore TrustedPublisher AirBridgeTestCert.cer
```

### Step 4 — Sign the driver DLL

```cmd
signtool sign /fd sha256 /s PrivateCertStore /n "AirBridgeTestCert" /t http://timestamp.digicert.com AirBridge.IddDriver.dll
```

### Step 5 — Generate and sign the catalog file

If the WDK build did not produce `AirBridge.IddDriver.inf.cat`, generate it:

```cmd
inf2cat /driver:. /os:10_x64
```

This reads `AirBridge.IddDriver.inf` and produces `AirBridge.IddDriver.inf.cat`.

Sign the catalog:

```cmd
signtool sign /fd sha256 /s PrivateCertStore /n "AirBridgeTestCert" /t http://timestamp.digicert.com AirBridge.IddDriver.inf.cat
```

Verify the signature:

```cmd
signtool verify /pa /v AirBridge.IddDriver.inf.cat
```

### Step 6 — Install with devcon

Download `devcon.exe` from the WDK samples or via WinGet:

```cmd
winget install --id Microsoft.WindowsDriverKit.DevCon
```

From the `x64\Debug\` directory (elevated Command Prompt):

```cmd
devcon.exe install AirBridge.IddDriver.inf root\AirBridgeIdd
```

Expected output:

```
Device node created. Install is complete when drivers are installed...
Updating drivers for root\AirBridgeIdd from <path>\AirBridge.IddDriver.inf...
Drivers installed successfully.
```

### Step 7 — Verify the driver loaded

1. Open **Device Manager** (Win+X → Device Manager).
2. Expand **Display adapters**.
3. Confirm **AirBridge Virtual Display (IddCx)** is listed with no yellow warning icon.
4. Open **Display Settings** (right-click Desktop → Display settings) — a second
   display (2560x1600) should appear.

---

## Running the Feature End-to-End

1. Pair the Android tablet with the Windows PC using the AirBridge app.
2. In the Windows AirBridge app, start a **Tablet Display** session.
3. `TabletDisplaySession.cs` connects to `\\.\pipe\AirBridgeIdd` (10-second timeout).
4. The driver begins encoding and streaming frames; the tablet renders them
   full-screen via `TabletDisplaySession.kt`.

---

## Uninstallation

From an elevated Command Prompt:

```cmd
devcon.exe remove root\AirBridgeIdd
```

The virtual display adapter and monitor entry are removed immediately. No reboot
is required for uninstallation.

To disable test-signing after you are done:

```cmd
bcdedit /set testsigning off
```

Reboot. The "Test Mode" watermark disappears.

---

## Production Signing

For a production release the driver `.dll` and `.inf.cat` must be submitted to the
Microsoft Hardware Dev Center for **attestation signing**:

- URL: https://partner.microsoft.com/en-us/dashboard/hardware
- Requirements: Extended Validation (EV) code-signing certificate + WHCP account
- Test-signing must be disabled on user machines (`bcdedit /set testsigning off`
  is the default). Only WHQL-attested drivers load in normal (non-test) mode.

---

## INF Structure Overview

| INF section | Purpose |
|-------------|---------|
| `[Version]` | `Class=Display`, `ClassGuid={4D36E968...}`, `CatalogFile`, `DriverVer` |
| `[Standard.NTamd64]` | Maps hardware ID `root\AirBridgeIdd` to the install section |
| `[AirBridgeIdd_Install.NT]` | `CopyFiles` — copies the UMDF2 DLL to DriverStore (dir 13) |
| `[AirBridgeIdd_Install.NT.Services]` | Adds WUDFRd.sys (the UMDF reflector) as a kernel service |
| `[AirBridgeIdd_Install.NT.Wdf]` | Declares the UMDF service and its ordering; sets UMDF policy flags |
| `[AirBridgeIdd_Install_UmdfService]` | Points WDF to the DLL in DriverStore; sets UMDF library version |

---

## Troubleshooting

### Driver fails to install — "The third-party INF does not contain digital signature information"

- The catalog is not signed. Repeat Steps 4–5 in the install sequence above.
- Confirm `bcdedit /enum {current}` shows `testsigning Yes`.

### Driver installs but has a yellow warning icon in Device Manager

- The DLL loaded but IddCx initialisation failed.
- Check Event Viewer → Windows Logs → System for UMDF error events from source `WudfUsbccid` or `AirBridge.IddDriver`.
- Common cause: missing IddCx runtime. Ensure Windows is updated (IddCx ships with Windows).

### Named pipe `\\.\pipe\AirBridgeIdd` does not appear

- The driver may have failed at `IddCxAdapterInitAsync`. Check System event log.
- Confirm the device has no yellow icon (see above).
- Try uninstalling and reinstalling the driver.

### `TabletDisplaySession` times out connecting to the pipe (10-second timeout)

- The driver creates the pipe server only after adapter initialisation completes.
  If the driver is still loading, retry. If it consistently times out, the pipe
  was not created — check the event log for adapter init failures.

### Test-signing watermark does not appear after `bcdedit /set testsigning on`

- Secure Boot is still enabled in UEFI firmware. Disable it and try again.
- Run `msinfo32` and check "Secure Boot State" — must show "Off".

### `inf2cat` fails with "no applicable OS specified"

- Pass the correct OS string: `/os:10_x64` for Windows 10/11 x64.
- Ensure the INF `[Version]` section has a valid `DriverVer` date (MM/DD/YYYY).
