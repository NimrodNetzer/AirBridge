// Driver.cpp — AirBridge Indirect Display Driver entry point
//
// This is the UMDF2 DriverEntry function. WDF calls AirBridgeIdd_EvtDeviceAdd
// (defined in Device.cpp) when the OS enumerates our virtual device via the INF.

#include "Driver.h"

// Suppress "unreferenced formal parameter" warnings that are common in WDK callbacks.
#pragma warning(disable: 4100)

/// <summary>
/// Driver entry point — called by the OS when the driver is loaded.
/// Registers the WDF driver with a single device-add callback.
/// </summary>
NTSTATUS DriverEntry(
    _In_ PDRIVER_OBJECT  pDriverObject,
    _In_ PUNICODE_STRING pRegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, AirBridgeIdd_EvtDeviceAdd);

    return WdfDriverCreate(
        pDriverObject,
        pRegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES,
        &config,
        WDF_NO_HANDLE);
}
