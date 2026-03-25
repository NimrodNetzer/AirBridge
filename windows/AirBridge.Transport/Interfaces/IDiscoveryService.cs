using AirBridge.Core.Models;

namespace AirBridge.Transport.Interfaces;

/// <summary>
/// Advertises this device on the local network and discovers peer devices
/// using mDNS (service type: _airbridge._tcp.local).
/// </summary>
public interface IDiscoveryService : IDisposable
{
    /// <summary>Starts advertising this device and scanning for peers.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops advertising and scanning.</summary>
    Task StopAsync();

    /// <summary>Returns all currently visible devices on the network.</summary>
    IReadOnlyList<DeviceInfo> GetVisibleDevices();

    /// <summary>Raised when a new device is found.</summary>
    event EventHandler<DeviceInfo> DeviceFound;

    /// <summary>Raised when a previously found device disappears.</summary>
    event EventHandler<string> DeviceLost;  // arg: deviceId
}
