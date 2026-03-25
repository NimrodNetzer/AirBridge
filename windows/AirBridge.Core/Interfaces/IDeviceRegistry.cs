using AirBridge.Core.Models;

namespace AirBridge.Core.Interfaces;

/// <summary>
/// Manages the local registry of known and paired devices.
/// Implementations must be thread-safe.
/// </summary>
public interface IDeviceRegistry
{
    /// <summary>Returns all currently known devices (discovered or paired).</summary>
    IReadOnlyList<DeviceInfo> GetAllDevices();

    /// <summary>Returns only devices that have been successfully paired.</summary>
    IReadOnlyList<DeviceInfo> GetPairedDevices();

    /// <summary>Adds or updates a device in the registry.</summary>
    void AddOrUpdate(DeviceInfo device);

    /// <summary>Removes a device by its ID. Does nothing if not found.</summary>
    void Remove(string deviceId);

    /// <summary>Returns true if a device with the given ID exists and is paired.</summary>
    bool IsPaired(string deviceId);

    /// <summary>Raised when the registry contents change.</summary>
    event EventHandler<DeviceInfo> DeviceChanged;
}
