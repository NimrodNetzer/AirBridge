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
/// <para>
/// PING (0xF0) / PONG (0xF1) keepalive frames are handled internally and never
/// surfaced to callers via <see cref="ReceiveAsync"/>.  Call
/// <see cref="StartKeepaliveAsync"/> after construction to enable the keepalive loop.
/// </para>
/// </summary>
public sealed class TlsMessageChannel : IMessageChannel
{
    private readonly SslStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _connected = true;
    private bool _disposed;

    // ── Keepalive state ────────────────────────────────────────────────────
    private static readonly TimeSpan DefaultKeepaliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPongTimeout       = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _keepaliveInterval;
    private readonly TimeSpan _pongTimeout;

    /// <summary>
    /// Linked to the caller's token AND cancelled in <see cref="DisposeAsync"/>.
    /// Ensures the keepalive loop stops immediately when the channel is disposed,
    /// even if the caller's token is long-lived (e.g. the listener's lifetime token).
    /// </summary>
    private CancellationTokenSource? _keepaliveCts;

    /// <summary>
    /// UTC timestamp of the last PONG received from the peer.
    /// Updated by the receive loop whenever a PONG arrives.
    /// </summary>
    private DateTime _lastPongUtc = DateTime.UtcNow;

    // ── IMessageChannel ────────────────────────────────────────────────────

    /// <summary>
    /// Identifier of the remote peer.  Set to the peer's IP address initially;
    /// updated to the peer's stable device ID after the HANDSHAKE exchange.
    /// </summary>
    public string RemoteDeviceId { get; internal set; }

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
    /// <param name="keepaliveInterval">How often to send a PING. Defaults to 30 s.</param>
    /// <param name="pongTimeout">How long to wait for a PONG before closing. Defaults to 10 s.</param>
    public TlsMessageChannel(
        SslStream stream,
        string remoteDeviceId,
        TimeSpan? keepaliveInterval = null,
        TimeSpan? pongTimeout = null)
    {
        _stream            = stream        ?? throw new ArgumentNullException(nameof(stream));
        RemoteDeviceId     = remoteDeviceId ?? throw new ArgumentNullException(nameof(remoteDeviceId));
        _keepaliveInterval = keepaliveInterval ?? DefaultKeepaliveInterval;
        _pongTimeout       = pongTimeout       ?? DefaultPongTimeout;
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

        // Guard: if the channel has already been disposed (e.g. session replacement
        // disposed the old channel while a file-transfer chunk was mid-flight), fail
        // fast instead of letting _writeLock.WaitAsync throw ObjectDisposedException
        // from the SemaphoreSlim internals, which would surface as an unhandled exception.
        if (_disposed)
            throw new ObjectDisposedException(nameof(TlsMessageChannel), $"Cannot send on a disposed channel (remote={RemoteDeviceId}).");

        bool lockAcquired = false;
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockAcquired = true;
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkException(ex))
        {
            AirBridge.Core.AppLog.Warn($"[{RemoteDeviceId}] Send failed (type={message.Type}) — {ex.GetType().Name}: {ex.Message}");
            SignalDisconnect();
            throw;
        }
        finally
        {
            if (lockAcquired && !_disposed) _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <returns>
    /// The next <see cref="ProtocolMessage"/>, or <c>null</c> if the peer closed
    /// the connection cleanly (zero bytes read on the header).
    /// PING and PONG messages are handled internally and are never returned to callers.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the payload length in the frame header exceeds
    /// <see cref="ProtocolMessage.MaxPayloadBytes"/>.
    /// </exception>
    public async Task<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
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
                // On Windows, SslStream throws a SocketException (connection reset /
                // forcibly closed) when the remote side closes without a TLS close_notify.
                // Treat any network exception during the header read as a clean disconnect
                // and return null — the same as a graceful 0-byte EOF.
                AirBridge.Core.AppLog.Warn($"[{RemoteDeviceId}] Silent socket death detected during header read — {ex.GetType().Name}: {ex.Message}");
                SignalDisconnect();
                return null;
            }

            if (bytesRead == 0)
            {
                // Clean close from remote side (graceful TLS close_notify)
                AirBridge.Core.AppLog.Info($"[{RemoteDeviceId}] Clean EOF — peer closed TLS connection gracefully");
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
                AirBridge.Core.AppLog.Warn($"[{RemoteDeviceId}] Silent socket death detected during body read (payloadLen={payloadLength}) — {ex.GetType().Name}: {ex.Message}");
                SignalDisconnect();
                throw;
            }

            if (bytesRead == 0)
            {
                AirBridge.Core.AppLog.Info($"[{RemoteDeviceId}] Unexpected EOF mid-frame (payloadLen={payloadLength})");
                _connected = false;
                return null;
            }

            var type    = (MessageType)body[0];
            var payload = body[1..];

            // ── Keepalive intercept ───────────────────────────────────────────
            if (type == MessageType.Ping)
            {
                // Reply with PONG immediately; do not surface PING to caller
                try
                {
                    await SendAsync(
                        new ProtocolMessage(MessageType.Pong, Array.Empty<byte>()),
                        cancellationToken).ConfigureAwait(false);
                }
                catch { /* best-effort; if send fails the loop will catch it on next read */ }
                continue; // read next message
            }

            if (type == MessageType.Pong)
            {
                _lastPongUtc = DateTime.UtcNow;
                continue; // do not surface PONG to caller
            }

            return new ProtocolMessage(type, payload);
        }
    }

    // ── Keepalive ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a background keepalive loop that sends a PING every 30 seconds and
    /// closes the channel if no PONG arrives within 10 seconds.
    /// Should be called once, immediately after the HANDSHAKE exchange completes.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token that, when cancelled, gracefully terminates the keepalive loop.
    /// </param>
    public Task StartKeepaliveAsync(CancellationToken cancellationToken)
    {
        // Create an internal CTS linked to the caller's token so we can independently
        // cancel the keepalive loop from DisposeAsync, regardless of how long-lived
        // the caller's token is.
        _keepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return Task.Run(() => KeepaliveLoopAsync(_keepaliveCts.Token), _keepaliveCts.Token);
    }

    private async Task KeepaliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _connected)
            {
                await Task.Delay(_keepaliveInterval, cancellationToken).ConfigureAwait(false);

                if (!_connected) break;

                var pingTime = DateTime.UtcNow;
                AirBridge.Core.AppLog.Info($"[{RemoteDeviceId}] PING sent");
                try
                {
                    await SendAsync(
                        new ProtocolMessage(MessageType.Ping, Array.Empty<byte>()),
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Send failure already signals disconnect; stop the loop
                    break;
                }

                // Wait for PONG (poll at 250ms intervals up to _pongTimeout)
                var deadline = pingTime + _pongTimeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (_lastPongUtc >= pingTime) break;
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }

                if (_lastPongUtc < pingTime)
                {
                    // No PONG received within timeout — treat as dead connection
                    AirBridge.Core.AppLog.Warn($"[{RemoteDeviceId}] PONG timeout — closing channel");
                    SignalDisconnect();
                    await DisposeAsync().ConfigureAwait(false);
                    break;
                }

                AirBridge.Core.AppLog.Info($"[{RemoteDeviceId}] PONG received OK");
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    // ── IAsyncDisposable ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed  = true;
        _connected = false;
        // Cancel the keepalive loop immediately so it does not linger after the channel
        // is disposed.  Without this, the loop sits in Task.Delay(30 s) even after
        // _connected=false, causing ghost PINGs on dead channels minutes later.
        _keepaliveCts?.Cancel();
        _keepaliveCts?.Dispose();
        _keepaliveCts = null;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from <paramref name="stream"/>.
    /// Returns 0 if the very first read returns 0 (clean EOF before any bytes).
    /// Throws <see cref="EndOfStreamException"/> if the stream closes after some bytes
    /// have been read but before the buffer is full (mid-frame disconnect).
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
            {
                if (offset == 0)
                    return 0; // clean EOF — no bytes read yet
                // Mid-frame EOF: connection dropped after partial data
                throw new EndOfStreamException(
                    $"Connection closed after {offset} of {buffer.Length} expected bytes.");
            }
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
        ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException;
}
