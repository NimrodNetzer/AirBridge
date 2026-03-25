using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Transport.Connection;

/// <summary>
/// <see cref="IMessageChannel"/> implementation that sends and receives
/// <see cref="ProtocolMessage"/> objects over an <see cref="SslStream"/> using
/// a simple length-prefix wire format:
/// <code>
/// [4 bytes big-endian payload length][1 byte MessageType][N bytes payload]
/// </code>
/// <para>
/// The length field encodes the number of payload bytes only (not the type byte).
/// </para>
/// <para>Sends are serialised with a <see cref="SemaphoreSlim"/> so callers may
/// call <see cref="SendAsync"/> concurrently from multiple threads.</para>
/// </summary>
public sealed class TlsMessageChannel : IMessageChannel
{
    private readonly SslStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _connected = true;
    private bool _disposed;

    /// <inheritdoc/>
    public string RemoteDeviceId { get; }

    /// <inheritdoc/>
    public bool IsConnected => _connected;

    /// <inheritdoc/>
    public event EventHandler? Disconnected;

    /// <summary>
    /// Wraps an already-authenticated <see cref="SslStream"/> as a message channel.
    /// </summary>
    /// <param name="stream">An authenticated, open SSL stream.</param>
    /// <param name="remoteDeviceId">
    /// Identifier of the remote peer (filled from the Handshake message in higher layers;
    /// may be a placeholder until the handshake completes).
    /// </param>
    public TlsMessageChannel(SslStream stream, string remoteDeviceId)
    {
        _stream        = stream        ?? throw new ArgumentNullException(nameof(stream));
        RemoteDeviceId = remoteDeviceId ?? throw new ArgumentNullException(nameof(remoteDeviceId));
    }

    // ── IMessageChannel ────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Wire format: <c>[4-byte big-endian payload length][1-byte type][payload]</c>.
    /// Thread-safe via an internal semaphore.
    /// </remarks>
    public async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Payload.Length > ProtocolMessage.MaxPayloadBytes)
            throw new ArgumentException(
                $"Payload length {message.Payload.Length} exceeds MaxPayloadBytes ({ProtocolMessage.MaxPayloadBytes}).",
                nameof(message));

        // Frame: [4-byte length][1-byte type][payload]
        var frame = new byte[4 + 1 + message.Payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)message.Payload.Length);
        frame[4] = (byte)message.Type;
        message.Payload.CopyTo(frame, 5);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkException(ex))
        {
            SignalDisconnect();
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <returns>
    /// The next <see cref="ProtocolMessage"/>, or <c>null</c> if the peer closed
    /// the connection cleanly (zero bytes read on the header).
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the payload length in the frame header exceeds
    /// <see cref="ProtocolMessage.MaxPayloadBytes"/>.
    /// </exception>
    public async Task<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        // ── Read 4-byte length header ──────────────────────────────────────
        var header = new byte[4];
        int bytesRead;
        try
        {
            bytesRead = await ReadExactAsync(_stream, header, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkException(ex))
        {
            SignalDisconnect();
            throw;
        }

        if (bytesRead == 0)
        {
            // Clean close from remote side
            _connected = false;
            return null;
        }

        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header);

        if (payloadLength > ProtocolMessage.MaxPayloadBytes)
            throw new InvalidDataException(
                $"Incoming payload length {payloadLength} exceeds MaxPayloadBytes ({ProtocolMessage.MaxPayloadBytes}).");

        // ── Read 1-byte message type + payload ────────────────────────────
        var body = new byte[1 + payloadLength];
        try
        {
            bytesRead = await ReadExactAsync(_stream, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkException(ex))
        {
            SignalDisconnect();
            throw;
        }

        if (bytesRead == 0)
        {
            _connected = false;
            return null;
        }

        var type    = (MessageType)body[0];
        var payload = body[1..];

        return new ProtocolMessage(type, payload);
    }

    // ── IAsyncDisposable ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed  = true;
        _connected = false;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from <paramref name="stream"/>,
    /// returning 0 only if the very first read returns 0 (clean EOF).
    /// </summary>
    private static async Task<int> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                                .ConfigureAwait(false);
            if (n == 0)
                return offset; // EOF
            offset += n;
        }
        return offset;
    }

    private void SignalDisconnect()
    {
        if (_connected)
        {
            _connected = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsNetworkException(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException;
}
