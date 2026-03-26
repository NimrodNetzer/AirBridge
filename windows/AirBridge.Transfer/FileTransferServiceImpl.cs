using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Transfer.Interfaces;
using System.Collections.Concurrent;

namespace AirBridge.Transfer;

/// <summary>
/// Default implementation of <see cref="IFileTransferService"/>.
/// Uses <see cref="TransferQueue"/> to schedule sessions and
/// <see cref="TransferSession"/> for the actual chunked transfer.
/// </summary>
public sealed class FileTransferServiceImpl : IFileTransferService
{
    private readonly TransferQueue _queue = new(concurrency: 2);
    private readonly ConcurrentDictionary<string, ITransferSession> _sessions = new();

    /// <inheritdoc/>
    public async Task<ITransferSession> SendFileAsync(
        string filePath,
        DeviceInfo destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(destination);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found.", filePath);

        // The transport channel must be opened by the caller before this service
        // can write to it.  For the UI path, TransferViewModel.SendFileAsync opens
        // the channel and calls the channel-based overload below.
        throw new InvalidOperationException(
            "Use SendFileWithStreamAsync from the UI layer. " +
            "Call DeviceConnectionService.ConnectToDeviceAsync first.");
    }

    /// <summary>
    /// Sends a file using an already-open network stream pair.
    /// </summary>
    public async Task<ITransferSession> SendFileWithStreamAsync(
        string filePath,
        Stream networkStream,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(networkStream);

        var info      = new FileInfo(filePath);
        var sessionId = Guid.NewGuid().ToString("N");
        var dataStream = info.OpenRead();

        var session = new TransferSession(
            sessionId,
            info.Name,
            info.Length,
            isSender: true,
            dataStream: dataStream,
            networkStream: networkStream,
            progress: progress);

        _sessions[sessionId] = session;
        await _queue.EnqueueAsync(session, cancellationToken).ConfigureAwait(false);
        _sessions.TryRemove(sessionId, out _);
        dataStream.Dispose();
        return session;
    }

    /// <inheritdoc/>
    public async Task<ITransferSession> ReceiveFileAsync(
        string sessionId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(destinationDirectory);

        // Receiving requires the sender to have already sent a FileStart message
        // over the channel.  This is handled by the transport/pairing layer.
        throw new InvalidOperationException(
            "Use ReceiveFileWithStreamAsync from the transport/pairing layer.");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ITransferSession> GetActiveSessions()
        => _sessions.Values.ToList();

    /// <summary>Disposes the underlying queue.</summary>
    public void Dispose() => _queue.Dispose();
}
