// MonitorDescriptor.h — Fake EDID for the AirBridge virtual monitor (2560x1600)
//
// IddCx requires a valid-looking EDID in IDDCX_MONITOR_DESCRIPTION.
// The EDID here is manually constructed to represent a generic 2560x1600
// WQXGA display panel, which matches the target resolution for a large tablet.

#pragma once
#include <windows.h>

/// <summary>Length of the EDID blob in bytes (exactly 128 bytes, standard EDID 1.3).</summary>
constexpr DWORD AIRBRIDGE_EDID_SIZE = 128;

/// <summary>Returns a pointer to the statically-allocated EDID byte array.</summary>
const BYTE* AirBridgeGetEdid() noexcept;
