using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using System.Collections.Concurrent;

namespace AirBridge.Core;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IDeviceRegistry"/>.
/// Devices are not persisted across restarts; paired state is stored in KeyStore.
/// </summary>
public sealed class InMemoryDeviceRegistry : IDeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();

    /// <inheritdoc/>
    public event EventHandler<DeviceInfo>? DeviceChanged;

    /// <inheritdoc/>
    public IReadOnlyList<DeviceInfo> GetAllDevices()
        => _devices.Values.ToList();

    /// <inheritdoc/>
    public IReadOnlyList<DeviceInfo> GetPairedDevices()
        => _devices.Values.Where(d => d.IsPaired).ToList();

    /// <inheritdoc/>
    public void AddOrUpdate(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _devices[device.DeviceId] = device;
        DeviceChanged?.Invoke(this, device);
    }

    /// <inheritdoc/>
    public void Remove(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var removed))
            DeviceChanged?.Invoke(this, removed);
    }

    /// <inheritdoc/>
    public bool IsPaired(string deviceId)
        => _devices.TryGetValue(deviceId, out var d) && d.IsPaired;
}
