// Driver.h — AirBridge Indirect Display Driver
// Top-level declarations shared across all source files.
//
// Build requirements:
//   - Windows Driver Kit (WDK) 10.0.22000 or later
//   - Windows App SDK is NOT used here — this is a pure WDK/UMDF2 project
//   - Link: IddCx.lib, WdfCoInstaller.lib, d3d11.lib, dxgi.lib, mf.lib, mfuuid.lib
//
// This driver is a UMDF2 (User-Mode Driver Framework v2) Indirect Display driver.
// It creates one virtual monitor (2560x1600) and, once the OS assigns a swap chain,
// captures each frame, H.264-encodes it via Media Foundation, and writes encoded
// NAL units to the named pipe \\.\pipe\AirBridgeIdd for the C# host app to read.

#pragma once

// ── WDK / UMDF2 headers ────────────────────────────────────────────────────
#define UMDF_USING_NTSTATUS
#include <windows.h>
#include <wdf.h>
#include <IddCx.h>

// ── Direct3D / DXGI headers ────────────────────────────────────────────────
#include <d3d11.h>
#include <dxgi1_2.h>

// ── Media Foundation headers ───────────────────────────────────────────────
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mftransform.h>
#include <codecapi.h>

// ── Standard library ───────────────────────────────────────────────────────
#include <vector>
#include <cstdint>

// ── Forward declarations ───────────────────────────────────────────────────
extern "C" {
    DRIVER_INITIALIZE DriverEntry;
}

EVT_WDF_DRIVER_DEVICE_ADD AirBridgeIdd_EvtDeviceAdd;
