using AirBridge.Core.Models;

namespace AirBridge.Transport.Interfaces;

/// <summary>
/// Manages TLS 1.3 TCP connections to and from peer devices.
/// The Windows app acts as host (server) and can also initiate outbound connections.
/// </summary>
public interface IConnectionManager : IDisposable
{
    /// <summary>Starts listening for incoming connections on the configured port.</summary>
    Task StartListeningAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops listening and closes all active connections.</summary>
    Task StopAsync();

    /// <summary>Opens a connection to a remote device. Returns a message channel on success.</summary>
    Task<IMessageChannel> ConnectAsync(DeviceInfo remoteDevice, CancellationToken cancellationToken = default);

    /// <summary>Raised when a remote device connects to us.</summary>
    event EventHandler<IMessageChannel> ConnectionReceived;
}
