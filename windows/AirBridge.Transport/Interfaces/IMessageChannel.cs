using AirBridge.Transport.Protocol;

namespace AirBridge.Transport.Interfaces;

/// <summary>
/// Represents a framed message channel over a TLS TCP connection.
/// Handles length-prefix framing; callers work with typed <see cref="ProtocolMessage"/> objects.
/// Implementations must be safe for concurrent sends from multiple threads.
/// </summary>
public interface IMessageChannel : IAsyncDisposable
{
    string RemoteDeviceId { get; }
    bool IsConnected { get; }

    /// <summary>Sends a protocol message. Thread-safe.</summary>
    Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the next incoming message. Returns null if the channel is closed cleanly.
    /// Throws on transport error.
    /// </summary>
    Task<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised when the connection drops unexpectedly.</summary>
    event EventHandler Disconnected;
}
