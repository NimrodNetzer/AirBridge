using AirBridge.Core.Interfaces;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Mirror;

/// <summary>
/// Result type returned at <see cref="MirrorSession"/> module boundaries.
/// </summary>
public sealed record MirrorResult(bool IsSuccess, string? ErrorMessage = null)
{
    /// <summary>Singleton success result.</summary>
    public static readonly MirrorResult Success = new(true);
    /// <summary>Creates a failure result with the given message.</summary>
    public static MirrorResult Failure(string message) => new(false, message);
}

/// <summary>
/// Implements <see cref="IMirrorSession"/> for the Windows (consumer) side of a
/// phone-as-floating-window mirror session.
///
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item>Construct with an <see cref="IMessageChannel"/> and optional factories
///     for the decoder and window host.</item>
///   <item>Call <see cref="StartAsync"/> — this runs the receive loop. It processes:
///     <list type="bullet">
///       <item><see cref="MirrorStartMessage"/> → opens the window host and initializes
///         the decoder at the received resolution.</item>
///       <item><see cref="MirrorFrameMessage"/> → feeds each NAL unit to the decoder.</item>
///       <item><see cref="MirrorStopMessage"/> → closes the window and stops the loop.</item>
///     </list>
///   </item>
///   <item>Call <see cref="StopAsync"/> to initiate a graceful shutdown from the Windows side.</item>
/// </list>
/// </para>
///
/// <para>
/// The <see cref="IMirrorWindowHost"/> and <see cref="MirrorDecoder"/> are created lazily
/// when the first <see cref="MirrorStartMessage"/> arrives. Pass <c>null</c> for
/// <paramref name="windowFactory"/> to run in headless mode (useful in unit tests).
/// </para>
/// </summary>
public sealed class MirrorSession : IMirrorSession
{
    // ── Dependencies ────────────────────────────────────────────────────────

    private readonly IMessageChannel _channel;
    private readonly Func<IMirrorDecoder> _decoderFactory;
    private readonly Func<IMirrorDecoder, IMirrorWindowHost>? _windowFactory;

    // ── State ───────────────────────────────────────────────────────────────

    private MirrorState _state = MirrorState.Connecting;
    private readonly object _stateLock = new();
    private IMirrorDecoder?      _decoder;
    private IMirrorWindowHost?   _window;

    private CancellationTokenSource? _cts;

    // ── Input send queue ────────────────────────────────────────────────────
    // A channel is used so SendInputAsync is non-blocking and fire-and-forget safe.
    private readonly System.Threading.Channels.Channel<InputEventArgs> _inputQueue =
        System.Threading.Channels.Channel.CreateBounded<InputEventArgs>(
            new System.Threading.Channels.BoundedChannelOptions(64)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

    // ── IMirrorSession ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string SessionId { get; }

    /// <inheritdoc/>
    public MirrorMode Mode => MirrorMode.PhoneWindow;

    /// <inheritdoc/>
    public MirrorState State
    {
        get { lock (_stateLock) return _state; }
        private set
        {
            lock (_stateLock) _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    /// <inheritdoc/>
    public event EventHandler<MirrorState>? StateChanged;

    // ── Constructor ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="MirrorSession"/>.
    /// </summary>
    /// <param name="sessionId">Unique identifier for this session.</param>
    /// <param name="channel">
    ///   The TLS message channel connected to the Android peer.
    /// </param>
    /// <param name="decoderFactory">
    ///   Optional factory for <see cref="IMirrorDecoder"/>. Defaults to
    ///   <c>() => new MirrorDecoder()</c>. Inject a no-op stub in unit tests.
    /// </param>
    /// <param name="windowFactory">
    ///   Optional factory for the <see cref="IMirrorWindowHost"/>. Pass <c>null</c>
    ///   to run headless (unit test mode — no window is opened).
    /// </param>
    public MirrorSession(
        string sessionId,
        IMessageChannel channel,
        Func<IMirrorDecoder>? decoderFactory = null,
        Func<IMirrorDecoder, IMirrorWindowHost>? windowFactory = null)
    {
        SessionId       = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _channel        = channel   ?? throw new ArgumentNullException(nameof(channel));
        _decoderFactory = decoderFactory ?? (() => (IMirrorDecoder)new MirrorDecoder());
        _windowFactory  = windowFactory;
    }

    // ── IMirrorSession lifecycle ────────────────────────────────────────────

    /// <summary>
    /// Starts the receive loop. Blocks until a <see cref="MirrorStopMessage"/> is
    /// received, the channel disconnects, or <see cref="StopAsync"/> is called.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state != MirrorState.Connecting)
                throw new InvalidOperationException($"Cannot start in state {_state}.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var receiveTask = RunReceiveLoopAsync(_cts.Token);
            var sendTask    = RunInputSendLoopAsync(_cts.Token);
            await Task.WhenAll(receiveTask, sendTask).ConfigureAwait(false);
        }
        finally
        {
            _inputQueue.Writer.TryComplete();
            if (State is not MirrorState.Stopped and not MirrorState.Error)
                State = MirrorState.Stopped;
            CleanUp();
        }
    }

    /// <summary>
    /// Gracefully stops the session: cancels the receive loop and releases resources.
    /// </summary>
    public Task StopAsync()
    {
        State = MirrorState.Stopped;
        _inputQueue.Writer.TryComplete();
        _cts?.Cancel();
        CleanUp();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enqueues an input event for forwarding to the connected Android device.
    /// The event is serialized as an <see cref="InputEventMessage"/> and sent over
    /// the <see cref="IMessageChannel"/>.
    /// </summary>
    /// <remarks>
    /// The method is non-blocking: events are queued and sent by a background loop.
    /// If the queue is full the oldest pending event is dropped to preserve low latency.
    /// </remarks>
    public Task SendInputAsync(
        InputEventArgs inputEvent,
        CancellationToken cancellationToken = default)
    {
        if (inputEvent is null) return Task.CompletedTask;
        _inputQueue.Writer.TryWrite(inputEvent);
        return Task.CompletedTask;
    }

    // ── Receive loop ────────────────────────────────────────────────────────

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ProtocolMessage? msg;
            try
            {
                msg = await _channel.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                State = MirrorState.Error;
                return;
            }

            if (msg is null) break; // clean channel close

            try
            {
                switch (msg.Type)
                {
                    case MessageType.MirrorStart:
                        await HandleMirrorStartAsync(msg.Payload, ct).ConfigureAwait(false);
                        break;

                    case MessageType.MirrorFrame:
                        HandleMirrorFrame(msg.Payload);
                        break;

                    case MessageType.MirrorStop:
                        State = MirrorState.Stopped;
                        _window?.Close();
                        return;

                    default:
                        // Unknown message type — ignore and continue
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                State = MirrorState.Error;
                return;
            }
        }
    }

    private async Task HandleMirrorStartAsync(byte[] payload, CancellationToken ct)
    {
        MirrorStartMessage startMsg;
        try
        {
            startMsg = MirrorStartMessage.FromBytes(payload);
        }
        catch
        {
            State = MirrorState.Error;
            return;
        }

        if (startMsg.Width <= 0 || startMsg.Height <= 0)
        {
            State = MirrorState.Error;
            return;
        }

        _decoder = _decoderFactory.Invoke();
        await _decoder.InitializeAsync(startMsg.Width, startMsg.Height).ConfigureAwait(false);

        if (_windowFactory is not null)
        {
            _window = _windowFactory(_decoder);

            // Subscribe to input events raised by the window so we can relay them to Android
            if (_window is MirrorWindow mw)
                mw.InputEventRaised += (_, evt) => _inputQueue.Writer.TryWrite(evt);

            _window.Open(startMsg.Width, startMsg.Height);
        }

        State = MirrorState.Active;
    }

    private void HandleMirrorFrame(byte[] payload)
    {
        if (_decoder is null) return;

        MirrorFrameMessage frameMsg;
        try
        {
            frameMsg = MirrorFrameMessage.FromBytes(payload);
        }
        catch
        {
            return; // malformed frame — skip
        }

        _decoder.SubmitNalUnit(frameMsg.NalData, frameMsg.TimestampMs, frameMsg.IsKeyFrame);
    }

    // ── Input send loop ──────────────────────────────────────────────────────

    /// <summary>
    /// Drains <see cref="_inputQueue"/> and sends each event to the Android peer
    /// as an <see cref="InputEventMessage"/> with <see cref="MessageType.InputEvent"/>.
    /// Runs concurrently with the receive loop; stops when the token is cancelled
    /// or the queue is completed.
    /// </summary>
    private async Task RunInputSendLoopAsync(CancellationToken ct)
    {
        await foreach (var evt in _inputQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // Map InputEventType → InputEventKind
            var kind = evt.Type switch
            {
                InputEventType.Touch => InputEventKind.Touch,
                InputEventType.Key   => InputEventKind.Key,
                _                    => InputEventKind.Mouse
            };

            var msg = new InputEventMessage(
                SessionId:   SessionId,
                EventKind:   kind,
                NormalizedX: evt.NormalizedX,
                NormalizedY: evt.NormalizedY,
                Keycode:     evt.Keycode,
                MetaState:   evt.MetaState);

            try
            {
                await _channel.SendAsync(
                    new Transport.Protocol.ProtocolMessage(
                        Transport.Protocol.MessageType.InputEvent,
                        msg.ToBytes()),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Best-effort: drop the event if the channel is gone
            }
        }
    }

    // ── Cleanup ─────────────────────────────────────────────────────────────

    private void CleanUp()
    {
        try { _window?.Close(); }   catch { }
        try { _decoder?.Dispose(); } catch { }
        _decoder = null;
        _window  = null;
    }

    // ── IDisposable ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        CleanUp();
    }
}
