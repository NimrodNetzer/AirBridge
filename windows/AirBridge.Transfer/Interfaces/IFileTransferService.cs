using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;

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
}
