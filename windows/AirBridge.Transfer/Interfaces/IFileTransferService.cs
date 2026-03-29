using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Transfer.Interfaces;

/// <summary>
/// High-level file transfer service. Creates and manages transfer sessions.
/// Implemented in Iteration 4.
/// </summary>
public interface IFileTransferService
{
    /// <summary>Initiates sending a file to a paired remote device.</summary>
    Task<ITransferSession> SendFileAsync(string filePath, DeviceInfo destination, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an incoming file transfer from a remote device.
    /// <paramref name="destinationDirectory"/> is where the file will be saved.
    /// </summary>
    Task<ITransferSession> ReceiveFileAsync(string sessionId, string destinationDirectory, CancellationToken cancellationToken = default);

    /// <summary>Returns all active or recent transfer sessions.</summary>
    IReadOnlyList<ITransferSession> GetActiveSessions();

    /// <summary>
    /// Returns a message handler that processes inbound FILE_TRANSFER_START/CHUNK/END messages
    /// and saves received files to the AirBridge Downloads folder.
    /// Register with <c>DeviceConnectionService.AddMessageHandler</c> after a session is established.
    /// </summary>
    Func<ProtocolMessage, Task> CreateReceiveHandler();

    /// <summary>
    /// Sets the active outbound channel for <see cref="SendFileAsync"/>.
    /// Call with <see langword="null"/> when the device disconnects.
    /// </summary>
    void SetChannel(IMessageChannel? channel);

    /// <summary>
    /// Clears the active channel only if it is still <paramref name="expected"/>.
    /// Safe to call from a disconnected session's finally block without wiping a newly registered channel.
    /// </summary>
    void ClearChannel(IMessageChannel expected);
}
