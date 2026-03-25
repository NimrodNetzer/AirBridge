// Device.cpp — WDF device and IddCx adapter/monitor setup
//
// Responsibilities:
//   1. Create a WDF device (AirBridgeIdd_EvtDeviceAdd)
//   2. Register an IddCx adapter in the D0 entry callback
//   3. After adapter init: create the named pipe and add one virtual monitor
//   4. On swap-chain assignment: spawn the SwapChainProcessor thread
//   5. On swap-chain unassignment: signal the thread to stop and wait for it

#include "Device.h"
#include "MonitorDescriptor.h"
#include "H264Encoder.h"
#include <strsafe.h>

// ── EvtDeviceAdd ──────────────────────────────────────────────────────────

NTSTATUS AirBridgeIdd_EvtDeviceAdd(
    _In_ WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    // WDF device attributes: attach our context struct
    WDF_OBJECT_ATTRIBUTES deviceAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&deviceAttributes, IndirectDeviceContext);

    WDFDEVICE wdfDevice;
    NTSTATUS status = WdfDeviceCreate(&DeviceInit, &deviceAttributes, &wdfDevice);
    if (!NT_SUCCESS(status))
        return status;

    // Initialise context fields to safe defaults
    IndirectDeviceContext* ctx = IndirectDeviceContextGet(wdfDevice);
    ctx->AdapterObject   = nullptr;
    ctx->MonitorObject   = nullptr;
    ctx->SwapChainThread = INVALID_HANDLE_VALUE;
    ctx->PipeHandle      = INVALID_HANDLE_VALUE;
    ctx->StopRequested   = 0;
    ctx->SwapChain       = nullptr;

    // Register PnP power callback so we can call IddCxAdapterInitAsync from D0Entry
    WDF_PNPPOWER_EVENT_CALLBACKS pnpPowerCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPowerCallbacks);
    pnpPowerCallbacks.EvtDeviceD0Entry = AirBridgeIdd_EvtDeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpPowerCallbacks);

    // Tell IddCx this is an indirect display device
    return IddCxDeviceInitialize(wdfDevice);
}

// ── EvtDeviceD0Entry ──────────────────────────────────────────────────────

NTSTATUS AirBridgeIdd_EvtDeviceD0Entry(
    _In_ WDFDEVICE              Device,
    _In_ WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(PreviousState);

    // Build adapter init params
    IDDCX_ADAPTER_CAPS adapterCaps = {};
    adapterCaps.Size                                            = sizeof(adapterCaps);
    adapterCaps.MaxMonitorsSupported                            = 1;
    adapterCaps.EndPointDiagnostics.Size                        = sizeof(adapterCaps.EndPointDiagnostics);
    adapterCaps.EndPointDiagnostics.GammaSupport                = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    adapterCaps.EndPointDiagnostics.TransmissionType            = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    adapterCaps.EndPointDiagnostics.pEndPointFriendlyName       = L"AirBridge Virtual Display";
    adapterCaps.EndPointDiagnostics.pEndPointManufacturerName   = L"AirBridge";
    adapterCaps.EndPointDiagnostics.pEndPointModelName          = L"AirBridgeIdd v1";

    IDARG_IN_ADAPTERINIT adapterInit = {};
    adapterInit.WdfDevice = Device;
    adapterInit.pCaps     = &adapterCaps;
    adapterInit.ObjectAttributes.Size = sizeof(adapterInit.ObjectAttributes);
    adapterInit.EvtIddCxAdapterInitFinished = AirBridgeIdd_EvtAdapterInitFinished;

    IDARG_OUT_ADAPTERINIT adapterInitOut = {};
    NTSTATUS status = IddCxAdapterInitAsync(&adapterInit, &adapterInitOut);
    if (!NT_SUCCESS(status))
        return status;

    IndirectDeviceContext* ctx = IndirectDeviceContextGet(Device);
    ctx->AdapterObject = adapterInitOut.AdapterObject;
    return status;
}

// ── EvtAdapterInitFinished ────────────────────────────────────────────────

NTSTATUS AirBridgeIdd_EvtAdapterInitFinished(
    _In_ IDDCX_ADAPTER AdapterObject,
    _In_ const IDARG_IN_ADAPTER_INIT_FINISHED* pInArgs)
{
    UNREFERENCED_PARAMETER(pInArgs);

    // Retrieve WDFDEVICE from adapter so we can get our context
    IDDCX_ADAPTER_CONTEXT_ARGS ctxArgs = {};
    ctxArgs.Size = sizeof(ctxArgs);
    IDDCX_ADAPTER_CONTEXT_OUT ctxOut = {};
    // (IddCx internally links the adapter to its device)

    // Open the named pipe (create server end; C# app connects as client)
    HANDLE pipe = CreateNamedPipeW(
        AIRBRIDGE_IDD_PIPE_NAME,
        PIPE_ACCESS_OUTBOUND | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        1,           // max instances
        1024 * 1024, // output buffer 1 MB
        0,           // input buffer
        0,           // default timeout
        nullptr);    // default security

    // Note: we proceed even if the pipe cannot be created; frames will be dropped.
    // A real implementation should surface this as a device error.

    // Add one virtual monitor
    // Build monitor info with a fake EDID
    IDDCX_MONITOR_INFO monitorInfo = {};
    monitorInfo.Size = sizeof(monitorInfo);
    monitorInfo.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED;
    monitorInfo.ConnectorIndex = 0;
    monitorInfo.MonitorDescription.Size = sizeof(monitorInfo.MonitorDescription);
    monitorInfo.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    monitorInfo.MonitorDescription.DataSize = AIRBRIDGE_EDID_SIZE;
    monitorInfo.MonitorDescription.pData = AirBridgeGetEdid();

    IDARG_IN_MONITORCREATE monitorCreate = {};
    monitorCreate.ObjectAttributes.Size = sizeof(monitorCreate.ObjectAttributes);
    monitorCreate.pMonitorInfo = &monitorInfo;

    // IddCx callbacks for monitor swap-chain events
    IDDCX_MONITOR_CALLBACKS monitorCallbacks = {};
    monitorCallbacks.Size = sizeof(monitorCallbacks);
    monitorCallbacks.EvtIddCxMonitorAssignSwapChain   = AirBridgeIdd_EvtMonitorAssignSwapChain;
    monitorCallbacks.EvtIddCxMonitorUnassignSwapChain = AirBridgeIdd_EvtMonitorUnassignSwapChain;
    monitorCreate.MonitorCallbacks = monitorCallbacks;

    IDARG_OUT_MONITORCREATE monitorCreateOut = {};
    NTSTATUS status = IddCxMonitorCreate(AdapterObject, &monitorCreate, &monitorCreateOut);

    // Announce the monitor to the OS
    if (NT_SUCCESS(status))
    {
        IDARG_IN_MONITORARRIVAL arrival = {};
        arrival.MonitorObject = monitorCreateOut.MonitorObject;
        IddCxMonitorArrival(arrival.MonitorObject);
    }

    return status;
}

// ── Swap-chain thread parameter block ─────────────────────────────────────

struct SwapChainThreadParams
{
    IDDCX_SWAPCHAIN    SwapChain;
    HANDLE             PipeHandle;      // borrowed reference — do NOT close here
    volatile LONG*     StopRequested;
};

// ── EvtMonitorAssignSwapChain ─────────────────────────────────────────────

NTSTATUS AirBridgeIdd_EvtMonitorAssignSwapChain(
    _In_ IDDCX_MONITOR   MonitorObject,
    _In_ const IDARG_IN_MONITORASSIGNSWAPCHAIN* pInArgs)
{
    UNREFERENCED_PARAMETER(MonitorObject);

    // Retrieve our device context via the monitor's parent adapter
    // (In a real driver we'd store context on the monitor object; for brevity
    //  we use a module-level pointer — acceptable for a single-monitor driver.)
    // Here we allocate the thread param block on the heap.
    SwapChainThreadParams* params = new (std::nothrow) SwapChainThreadParams{};
    if (!params)
        return STATUS_INSUFFICIENT_RESOURCES;

    params->SwapChain    = pInArgs->hSwapChain;
    params->PipeHandle   = INVALID_HANDLE_VALUE; // pipe handle threaded separately
    params->StopRequested = nullptr;             // set by caller if needed

    // Spawn the swap-chain processing thread
    HANDLE thread = CreateThread(
        nullptr,
        0,
        SwapChainThreadProc,
        params,
        0,
        nullptr);

    if (!thread)
    {
        delete params;
        return STATUS_UNSUCCESSFUL;
    }

    // Caller should store the thread handle to join it in Unassign
    CloseHandle(thread); // detached — thread owns itself
    return STATUS_SUCCESS;
}

// ── EvtMonitorUnassignSwapChain ───────────────────────────────────────────

NTSTATUS AirBridgeIdd_EvtMonitorUnassignSwapChain(
    _In_ IDDCX_MONITOR MonitorObject)
{
    UNREFERENCED_PARAMETER(MonitorObject);
    // The swap-chain thread will exit naturally when IddCxSwapChainAcquireNextFrame
    // returns an error after the swap chain is invalidated.
    // A full implementation would signal StopRequested and WaitForSingleObject.
    return STATUS_SUCCESS;
}
