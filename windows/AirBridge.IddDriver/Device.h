// Device.h — Device & adapter context for the AirBridge IddCx driver

#pragma once
#include "Driver.h"

// ── Named pipe used to stream H.264 NAL units to the C# host app ───────────
//
// The driver opens this pipe (as SERVER) in Device.cpp after adapter init.
// The C# TabletDisplaySession opens it as CLIENT and reads encoded frames.
// Protocol over the pipe:
//   Each "packet" = [4-byte big-endian NAL length] [NAL bytes]
// The C# side wraps the NAL bytes into a MirrorFrameMessage and sends them
// over the existing TLS channel to the Android tablet.
#define AIRBRIDGE_IDD_PIPE_NAME L"\\\\.\\pipe\\AirBridgeIdd"

// ── Target display parameters (fake 2560x1600 tablet) ─────────────────────
#define AIRBRIDGE_DISPLAY_WIDTH  2560
#define AIRBRIDGE_DISPLAY_HEIGHT 1600
#define AIRBRIDGE_DISPLAY_FPS    60

// ── Device context: one per WDF device object ──────────────────────────────
struct IndirectDeviceContext
{
    IDDCX_ADAPTER AdapterObject;  ///< IddCx adapter handle (one per device)
    IDDCX_MONITOR MonitorObject;  ///< IddCx monitor handle (one virtual monitor)

    // Swap-chain thread (spawned when OS assigns a swap chain)
    HANDLE SwapChainThread;       ///< Thread that calls IddCxSwapChainAcquireNextFrame

    // Named pipe to C# host
    HANDLE PipeHandle;            ///< INVALID_HANDLE_VALUE until adapter is ready

    // Volatile flag to signal the swap-chain thread to exit
    volatile LONG StopRequested;  ///< Set to 1 to request orderly shutdown

    // Current swap chain (set in EvtMonitorAssignSwapChain, cleared in Unassign)
    IDDCX_SWAPCHAIN SwapChain;
};

// WDF accessor — retrieves the IndirectDeviceContext* from a WDFDEVICE
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(IndirectDeviceContext, IndirectDeviceContextGet)

// ── Device callbacks (defined in Device.cpp) ──────────────────────────────
EVT_WDF_DEVICE_D0_ENTRY            AirBridgeIdd_EvtDeviceD0Entry;
IDDCX_ADAPTER_INIT_PARAMS          AirBridgeIdd_BuildAdapterInitParams();
EVT_IDD_CX_ADAPTER_INIT_FINISHED   AirBridgeIdd_EvtAdapterInitFinished;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN   AirBridgeIdd_EvtMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN AirBridgeIdd_EvtMonitorUnassignSwapChain;

// ── Swap-chain thread (defined in SwapChainProcessor.cpp) ─────────────────
DWORD WINAPI SwapChainThreadProc(_In_ LPVOID lpParam);
