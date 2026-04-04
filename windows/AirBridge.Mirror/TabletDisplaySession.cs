using AirBridge.Core.Interfaces;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Mirror;

/// <summary>
/// Windows-side session for the "tablet / iPad as second monitor" feature.
///
/// Capture pipeline:
///   DXGI Desktop Duplication (virtual display) → MF H.264 encoder → TLS channel → iPad
///
/// The virtual display is created by the Parsec Virtual Display Driver (free, WHQL-signed).
/// No kernel driver or Secure Boot changes are required.
///
/// Session direction: Windows (source) → iPad/tablet (sink).
/// Uses MirrorStart (0x20), MirrorFrame (0x21), MirrorStop (0x22) message types.
/// </summary>
public sealed class TabletDisplaySession : IMirrorSession
{
    // ── Constants ─────────────────────────────────────────────────────────

    private const byte DefaultFps     = 30;
    private const int  DefaultBitrate = 8_000_000; // 8 Mbps

    // ── Fields ────────────────────────────────────────────────────────────

    private readonly IMessageChannel         _channel;
    private readonly string                  _sessionId;

    // -1 = auto-select first non-primary monitor (the virtual display)
    private readonly int                     _monitorIndex;

    private MirrorState                      _state = MirrorState.Connecting;
    private CancellationTokenSource?         _cts;
    private Task?                            _pumpTask;
    private long                             _frameIndex;
    private bool                             _disposed;

    // ── Constructor ───────────────────────────────────────────────────────

    /// <param name="sessionId">Unique session identifier.</param>
    /// <param name="channel">Authenticated TLS channel to the iPad.</param>
    /// <param name="monitorIndex">
    ///   Index of the monitor to capture. -1 = auto (first non-primary).
    /// </param>
    public TabletDisplaySession(string sessionId, IMessageChannel channel, int monitorIndex = -1)
    {
        _sessionId    = sessionId    ?? throw new ArgumentNullException(nameof(sessionId));
        _channel      = channel      ?? throw new ArgumentNullException(nameof(channel));
        _monitorIndex = monitorIndex;
    }

    // ── IMirrorSession ────────────────────────────────────────────────────

    public string     SessionId => _sessionId;
    public MirrorMode Mode      => MirrorMode.TabletDisplay;

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

    public event EventHandler<MirrorState>? StateChanged;

    /// <summary>
    /// Starts the session:
    ///  1. Initialises DXGI capture and H.264 encoder.
    ///  2. Sends MirrorStart to the iPad.
    ///  3. Starts the background capture→encode→send loop.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != MirrorState.Connecting)
            throw new InvalidOperationException($"Session already in state {_state}.");

        State = MirrorState.Connecting;

        // Initialise DXGI capture (finds virtual monitor)
        var capture = new DxgiScreenCapture();
        capture.Start(_monitorIndex);

        int width  = capture.Width;
        int height = capture.Height;

        // Initialise H.264 encoder
        var encoder = new MfH264Encoder(width, height, DefaultFps, DefaultBitrate);
        encoder.Start();

        // Send MirrorStart so the iPad knows dimensions + codec
        var startMsg = new MirrorStartMessage(
            MirrorSessionMode.TabletDisplay,
            MirrorCodec.H264,
            (ushort)width,
            (ushort)height,
            DefaultFps,
            _sessionId);

        await _channel.SendAsync(
            new ProtocolMessage(MessageType.MirrorStart, startMsg.ToBytes()),
            cancellationToken).ConfigureAwait(false);

        _cts      = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pumpTask = PumpFramesAsync(capture, encoder, _cts.Token);

        State = MirrorState.Active;
    }

    /// <summary>Stops the session gracefully.</summary>
    public async Task StopAsync()
    {
        if (_state is MirrorState.Stopped or MirrorState.Error) return;

        _cts?.Cancel();
        if (_pumpTask is not null)
        {
            try   { await _pumpTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* swallow — shutting down */ }
        }

        try
        {
            await _channel.SendAsync(
                new ProtocolMessage(MessageType.MirrorStop, new MirrorStopMessage(0).ToBytes()))
                .ConfigureAwait(false);
        }
        catch { /* channel may already be closed */ }

        State = MirrorState.Stopped;
    }

    public Task SendInputAsync(InputEventArgs inputEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Returns a message handler to register with DeviceConnectionService so that
    /// inbound MIRROR_STOP from the iPad is routed here without a concurrent read.
    /// </summary>
    public Func<ProtocolMessage, Task> CreateMessageHandler(Func<Task>? onStop = null) =>
        async msg =>
        {
            if (msg.Type == MessageType.MirrorStop)
            {
                await StopAsync().ConfigureAwait(false);
                if (onStop is not null) await onStop().ConfigureAwait(false);
            }
        };

    // ── Frame pump ────────────────────────────────────────────────────────

    /// <summary>
    /// Background loop: captures BGRA frames via DXGI, encodes to H.264 via MF,
    /// and sends each NAL unit as a MirrorFrameMessage over the TLS channel.
    /// </summary>
    private async Task PumpFramesAsync(
        DxgiScreenCapture capture,
        MfH264Encoder encoder,
        CancellationToken ct)
    {
        using (capture)
        using (encoder)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Capture one frame (returns null if no new frame within timeout)
                    var bgraFrame = await Task.Run(() => capture.AcquireFrame(50), ct)
                                              .ConfigureAwait(false);
                    if (bgraFrame is null) continue;

                    // Encode to H.264 NAL units
                    List<byte[]> nals = await Task.Run(() => encoder.EncodeFrame(bgraFrame), ct)
                                                  .ConfigureAwait(false);

                    foreach (var nal in nals)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (nal.Length == 0) continue;

                        // Detect IDR (key frame): NAL type bits 4:0 == 5
                        bool isKeyFrame = (nal[0] & 0x1F) == 5;
                        long ptsUs      = (_frameIndex * 1_000_000L) / DefaultFps;
                        _frameIndex++;

                        var frameMsg = new MirrorFrameMessage(isKeyFrame, ptsUs, nal);
                        await _channel.SendAsync(
                            new ProtocolMessage(MessageType.MirrorFrame, frameMsg.ToBytes()),
                            ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception ex)
            {
                AirBridge.Core.AppLog.Error($"[TabletDisplay:{_sessionId}] Pump error: {ex.Message}");
                State = MirrorState.Error;
                return;
            }
        }

        if (State == MirrorState.Active)
            State = MirrorState.Stopped;
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
