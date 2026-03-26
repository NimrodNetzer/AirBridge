# AirBridge.IddDriver — IddCx Virtual Display Driver

## Overview

This is a **UMDF2** (User-Mode Driver Framework v2) Indirect Display Driver (IddCx)
that creates one virtual 2560×1600 monitor on a Windows PC. When the OS assigns a
swap chain to the monitor (i.e., an app window is displayed on it), the driver:

1. Captures each frame from the IddCx swap chain via `IddCxSwapChainAcquireNextFrame`
2. Copies the D3D11 texture to a CPU-accessible staging buffer
3. H.264-encodes the raw BGRA pixels via the Windows Media Foundation MFT
4. Writes length-prefixed NAL units to the named pipe `\\.\pipe\AirBridgeIdd`

The C# `TabletDisplaySession` reads from that pipe and sends each NAL unit as a
`MirrorFrameMessage (0x21)` over the existing TLS channel to the Android tablet.

---

## Build Prerequisites

| Requirement | Version |
|-------------|---------|
| Visual Studio 2022 | 17.x |
| Windows Driver Kit (WDK) | 10.0.22000 (Windows 11 SDK) |
| WDK VS Integration (WDK.vsix) | must match WDK version |
| Target platform | x64 (ARM64 also supported) |

Install the WDK from: https://learn.microsoft.com/en-us/windows-hardware/drivers/download-the-wdk

The WDK must be installed **alongside** the matching Windows SDK.

---

## Build Steps

1. Open `windows/AirBridge.sln` in Visual Studio 2022.
2. The `AirBridge.IddDriver` project uses the `WindowsUserModeDriver10.0` platform
   toolset — this resolves automatically if the WDK VS integration is installed.
3. Select **Debug | x64** and build the `AirBridge.IddDriver` project.
4. Output: `x64/Debug/AirBridge.IddDriver.dll` + `AirBridge.IddDriver.cat` (unsigned).

---

## Installation (Developer / Test Machines)

### 1. Enable test-signing (required for unsigned/self-signed drivers)

Open an **elevated** Command Prompt:

```cmd
bcdedit /set testsigning on
```

Reboot. A "Test Mode" watermark will appear on the desktop — this is expected.

### 2. Self-sign the driver (optional but recommended for test)

```cmd
makecert -r -pe -ss PrivateCertStore -n "CN=AirBridgeTestCert" AirBridgeTestCert.cer
signtool sign /fd sha256 /s PrivateCertStore /n "AirBridgeTestCert" AirBridge.IddDriver.dll
inf2cat /driver:. /os:10_x64
signtool sign /fd sha256 /s PrivateCertStore /n "AirBridgeTestCert" AirBridge.IddDriver.cat
```

### 3. Install with devcon

Download `devcon.exe` from the WDK samples or via WinGet:

```cmd
winget install --id Microsoft.WindowsDriverKit.DevCon
```

Then install:

```cmd
devcon.exe install AirBridge.IddDriver.inf root\AirBridgeIdd
```

You should see a new display adapter appear in Device Manager under
**Display adapters → AirBridge Virtual Display (IddCx)**.

Windows will also add a new monitor entry under **Monitors**.

### 4. Verify

- Open **Display Settings**: you should see a second display (2560×1600).
- Run `TabletDisplaySession` from the C# host app to start streaming to the tablet.

---

## Uninstallation

```cmd
devcon.exe remove root\AirBridgeIdd
```

To re-enable normal boot (if you're done with test-signing):

```cmd
bcdedit /set testsigning off
```

---

## Named Pipe Protocol

The driver writes H.264 NAL units to `\\.\pipe\AirBridgeIdd`.

**Packet format (binary, big-endian):**

```
[4 bytes — uint32 — NAL length N]
[N bytes — raw H.264 NAL unit]
```

The C# `TabletDisplaySession` opens this pipe as a client and wraps each NAL
packet in a `MirrorFrameMessage` for delivery over the TLS channel.

---

## Production Signing

For a production release the driver `.dll` and `.cat` must be submitted to the
Microsoft Hardware Dev Center (https://partner.microsoft.com/en-us/dashboard/hardware)
for **attestation signing**. This requires an Extended Validation (EV) code-signing
certificate and a valid Windows Hardware Compatibility Program (WHCP) account.

Test-signing must be disabled on production devices (`bcdedit /set testsigning off`
is the default). Only WHQL-signed drivers load without test mode.

---

## INF Structure Overview

| INF section | Purpose |
|-------------|---------|
| `[Version]` | Class=Display, ClassGuid={4D36E968...}, DriverVer |
| `[Standard.NTamd64]` | Associates hardware ID `root\AirBridgeIdd` with install section |
| `[AirBridgeIdd_Install.NT]` | CopyFiles — copies the UMDF2 DLL to DriverStore |
| `[AirBridgeIdd_Install.NT.Services]` | Installs WUDFRd.sys as the reflector service |
| `[AirBridgeIdd_Install.NT.Wdf]` | Declares UMDF service, library version |
