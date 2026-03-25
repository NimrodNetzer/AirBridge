using System.IO.Pipes;
using AirBridge.Core.Interfaces;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Mirror;

/// <summary>
/// Windows-side session for the "tablet as second monitor" feature.
/// Drives the IddCx virtual display and streams its H.264-encoded framebuffer
/// to an Android tablet over the existing TLS channel.
/// Mode: <see cref="MirrorMode.TabletDisplay"/>
/// </summary>
/// <remarks>
/// <para>
/// <b>IPC design:</b> The IddCx UMDF2 driver (<c>AirBridge.IddDriver.dll</c>)
/// creates the server end of the named pipe <c>\\.\pipe\AirBridgeIdd</c> after
/// its adapter initialises. This class connects as the client and reads
/// length-prefixed H.264 NAL packets:
/// </para>
/// <code>
///   [4-byte big-endian uint32 — NAL length N]
///   [N bytes — raw H.264 NAL unit]
/// </code>
/// <para>
/// Each NAL unit is wrapped in a <see cref="MirrorFrameMessage"/> and sent
/// through <paramref name="channel"/> to the Android tablet, which decodes and
/// renders it full-screen via <c>TabletDisplaySession.kt</c>.
/// </para>
/// <para>
/// <b>Session direction:</b> Windows (source) → Android (sink).
/// Uses the existing <c>MirrorStart (0x20)</c>, <c>MirrorFrame (0x21)</c>, and
/// <c>MirrorStop (0x22)</c> message types — no new protocol types are added.
/// </para>
/// </remarks>
public sealed class TabletDisplaySession : IMirrorSession
{
    // ── Constants ─────────────────────────────────────────────────────────

    private const string PipeName           = "AirBridgeIdd";
    private const int    PipeConnectTimeout = 10_000; // ms — wait for driver to be ready
    private const ushort DefaultWidth       = 2560;
    private const ushort DefaultHeight      = 1600;
    private const byte   DefaultFps         = 60;

    // ── Fields ────────────────────────────────────────────────────────────

    private readonly IMessageChannel      _channel;
    private readonly string               _sessionId;

    private MirrorState                   _state = MirrorState.Connecting;
    private NamedPipeClientStream?        _pipe;
    private CancellationTokenSource?      _cts;
    private Task?                         _pumpTask;

    // Frame counter for PTS calculation (microseconds at target fps)
    private long _frameIndex;
    private bool _disposed;

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Initialises a new <see cref="TabletDisplaySession"/>.
    /// </summary>
    /// <param name="sessionId">Unique session identifier (e.g. a GUID string).</param>
    /// <param name="channel">
    ///   The authenticated TLS message channel to the Android tablet.
    /// </param>
    public TabletDisplaySession(string sessionId, IMessageChannel channel)
    {
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _channel   = channel   ?? throw new ArgumentNullException(nameof(channel));
    }

    // ── IMirrorSession ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string SessionId => _sessionId;

    /// <inheritdoc/>
    public MirrorMode Mode => MirrorMode.TabletDisplay;

    /// <inheritdoc/>
    public MirrorState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    /// <inheritdoc/>
    public event EventHandler<MirrorState>? StateChanged;

    /// <summary>
    /// Starts the tablet display session.
    /// <list type="number">
    ///   <item>Sends <see cref="MirrorStartMessage"/> to the Android tablet.</item>
    ///   <item>Connects to the named pipe exposed by the IddCx driver.</item>
    ///   <item>Starts a background pump task that reads NAL units from the pipe
    ///         and forwards them as <see cref="MirrorFrameMessage"/> messages.</item>
    /// </list>
    /// The method returns as soon as the pump starts; the caller can monitor
    /// <see cref="StateChanged"/> for transitions to <see cref="MirrorState.Active"/>
    /// or <see cref="MirrorState.Error"/>.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the start sequence.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != MirrorState.Connecting)
            throw new InvalidOperationException($"Session is already in state {_state}.");

        State = MirrorState.Connecting;

        // Send MirrorStart to Android (TabletDisplay mode)
        var startMsg = new MirrorStartMessage(
            MirrorSessionMode.TabletDisplay,
            MirrorCodec.H264,
            DefaultWidth,
            DefaultHeight,
            DefaultFps,
            _sessionId);

        await _channel.SendAsync(
            new ProtocolMessage(MessageType.MirrorStart, startMsg.ToBytes()),
            cancellationToken).ConfigureAwait(false);

        // Connect to the named pipe created by the IddCx driver
        _pipe = new NamedPipeClientStream(
            ".",          // local machine
            PipeName,
            PipeDirection.In,
            PipeOptions.Asynchronous);

        await _pipe.ConnectAsync(PipeConnectTimeout, cancellationToken).ConfigureAwait(false);

        // Start frame pump
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pumpTask = PumpFramesAsync(_cts.Token);

        State = MirrorState.Active;
    }

    /// <summary>
    /// Stops the session gracefully: cancels the pump, sends
    /// <see cref="MirrorStopMessage"/> to Android, and closes the pipe.
    /// </summary>
    public async Task StopAsync()
    {
        if (_state is MirrorState.Stopped or MirrorState.Error)
            return;

        // Signal pump to stop
        _cts?.Cancel();
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* swallow — we're shutting down */ }
        }

        // Send MirrorStop to Android
        try
        {
            var stopMsg = new MirrorStopMessage(0);
            await _channel.SendAsync(
                new ProtocolMessage(MessageType.MirrorStop, stopMsg.ToBytes()))
                .ConfigureAwait(false);
        }
        catch (Exception) { /* channel may already be closed */ }

        _pipe?.Close();
        State = MirrorState.Stopped;
    }

    /// <summary>
    /// Not applicable for <see cref="MirrorMode.TabletDisplay"/> (Windows sends
    /// the display to Android; input events are not relayed in this direction).
    /// </summary>
    public Task SendInputAsync(InputEventArgs inputEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    // ── Frame pump ────────────────────────────────────────────────────────

    /// <summary>
    /// Background task: reads length-prefixed NAL units from the named pipe
    /// and sends each one as a <see cref="MirrorFrameMessage"/> over the channel.
    /// </summary>
    private async Task PumpFramesAsync(CancellationToken ct)
    {
        if (_pipe is null) return;

        var headerBuf = new byte[4];

        try
        {
            while (!ct.IsCancellationRequested && _pipe.IsConnected)
            {
                // Read 4-byte big-endian length prefix
                int read = await ReadExactAsync(_pipe, headerBuf, 0, 4, ct).ConfigureAwait(false);
                if (read == 0) break; // pipe closed cleanly

                int nalLen =
                    (headerBuf[0] << 24) |
                    (headerBuf[1] << 16) |
                    (headerBuf[2] <<  8) |
                     headerBuf[3];

                if (nalLen <= 0 || nalLen > ProtocolMessage.MaxPayloadBytes)
                    break; // malformed length

                var nalBuf = new byte[nalLen];
                read = await ReadExactAsync(_pipe, nalBuf, 0, nalLen, ct).ConfigureAwait(false);
                if (read < nalLen) break; // pipe closed mid-packet

                // Detect IDR (key frame): NAL unit type bits 4:0 == 5
                bool isKeyFrame = nalLen > 0 && (nalBuf[0] & 0x1F) == 5;

                // PTS in microseconds
                long ptsUs = (_frameIndex * 1_000_000L) / DefaultFps;
                _frameIndex++;

                var frameMsg = new MirrorFrameMessage(isKeyFrame, ptsUs, nalBuf);
                await _channel.SendAsync(
                    new ProtocolMessage(MessageType.MirrorFrame, frameMsg.ToBytes()),
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception)
        {
            State = MirrorState.Error;
            return;
        }

        if (State == MirrorState.Active)
            State = MirrorState.Stopped;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes from <paramref name="stream"/>
    /// into <paramref name="buf"/>, retrying until the buffer is full or the stream
    /// ends. Returns the number of bytes actually read (may be less than count if
    /// the stream closes cleanly).
    /// </summary>
    private static async Task<int> ReadExactAsync(
        Stream stream, byte[] buf, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(offset + totalRead, count - totalRead), ct)
                                .ConfigureAwait(false);
            if (n == 0) break; // EOF
            totalRead += n;
        }
        return totalRead;
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _pipe?.Dispose();
    }
}
