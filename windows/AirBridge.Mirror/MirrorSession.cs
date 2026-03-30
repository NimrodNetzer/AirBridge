using AirBridge.Core.Interfaces;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Mirror;

/// <summary>
/// Windows-side implementation of <see cref="IMirrorSession"/>.
/// <para>
/// Responsibilities:
/// <list type="bullet">
///   <item>Sends <see cref="MessageType.MirrorStart"/> to the Android device and
///         opens a floating window via the injected <see cref="IMirrorWindowHost"/>.</item>
///   <item>Feeds <see cref="MessageType.MirrorFrame"/> payloads to the
///         <see cref="IMirrorDecoder"/> and presents them in the window.</item>
///   <item>On <see cref="MessageType.MirrorStop"/>, closes the window and stops.</item>
///   <item>Captures pointer/keyboard events from the window and relays them to
///         Android as <see cref="InputEventMessage"/> (0x30) messages.</item>
///   <item>When files are dropped onto the window, calls <see cref="SendFileAsync"/>
///         which delegates to the injected <see cref="ITransferEngine"/>.</item>
///   <item>Passes through <see cref="MessageType.FileTransferAck"/> /
///         <c>FileTransferEnd</c> messages received from Android.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MirrorSession : IMirrorSession
{
    // ── Dependencies ───────────────────────────────────────────────────────

    private readonly IMessageChannel                          _channel;
    private readonly Func<IMirrorDecoder>?                    _decoderFactory;
    private readonly Func<IMirrorDecoder, IMirrorWindowHost>? _windowFactory;
    private readonly ITransferEngine?                         _transferEngine;
    private readonly IProgress<long>?                         _transferProgress;
    private readonly bool                                     _androidInitiated;
    private readonly int                                      _width;
    private readonly int                                      _height;

    // ── Runtime state ──────────────────────────────────────────────────────

    private MirrorState         _state = MirrorState.Connecting;
    private CancellationTokenSource? _cts;
    private IMirrorWindowHost?  _window;
    private IMirrorDecoder?     _decoder;
    private readonly object     _stateLock = new();

    // ── Inbound message queue ─────────────────────────────────────────────
    // MirrorSession must NOT call _channel.ReceiveAsync() directly because
    // DeviceConnectionService.MonitorSessionAsync is already the single reader
    // on the channel's SslStream (concurrent reads throw NotSupportedException).
    // Instead, the caller registers CreateMessageHandler() with DeviceConnectionService
    // so dispatched messages are written here and the session reads only from this queue.
    private readonly System.Threading.Channels.Channel<ProtocolMessage> _inboundQueue =
        System.Threading.Channels.Channel.CreateUnbounded<ProtocolMessage>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

    // ── Input send queue ───────────────────────────────────────────────────
    // Bounded channel so SendInputAsync is non-blocking and fire-and-forget safe.
    private readonly System.Threading.Channels.Channel<InputEventArgs> _inputQueue =
        System.Threading.Channels.Channel.CreateBounded<InputEventArgs>(
            new System.Threading.Channels.BoundedChannelOptions(64)
            {
                FullMode     = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

    // ── IMirrorSession ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string SessionId { get; }

    /// <inheritdoc/>
    public MirrorMode Mode { get; }

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
    /// <param name="channel">The transport channel to the paired Android device.</param>
    /// <param name="decoderFactory">Factory for <see cref="IMirrorDecoder"/>; null = headless.</param>
    /// <param name="windowFactory">Factory for <see cref="IMirrorWindowHost"/>; null = headless.</param>
    /// <param name="transferEngine">Engine for drag-and-drop file transfer; null = disabled.</param>
    /// <param name="transferProgress">Optional progress reporter (bytes transferred).</param>
    public MirrorSession(
        string sessionId,
        IMessageChannel channel,
        Func<IMirrorDecoder>? decoderFactory = null,
        Func<IMirrorDecoder, IMirrorWindowHost>? windowFactory = null,
        ITransferEngine? transferEngine = null,
        IProgress<long>? transferProgress = null,
        bool androidInitiated = false,
        int width = 0,
        int height = 0)
    {
        SessionId         = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _channel          = channel   ?? throw new ArgumentNullException(nameof(channel));
        Mode              = MirrorMode.PhoneWindow;
        _decoderFactory   = decoderFactory;
        _windowFactory    = windowFactory;
        _transferEngine   = transferEngine;
        _transferProgress = transferProgress;
        _androidInitiated = androidInitiated;
        _width            = width  > 0 ? width  : 1080;  // fallback for Windows-initiated
        _height           = height > 0 ? height : 2340;
    }

    // ── IMirrorSession lifecycle ───────────────────────────────────────────

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state != MirrorState.Connecting)
                throw new InvalidOperationException($"Cannot start a session in state {_state}.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        AirBridge.Core.AppLog.Info($"[Mirror:{SessionId}] Connecting → starting receive + input-send loops");

        try
        {
            // Run the message pump and input-send loop concurrently
            var receiveTask   = HandleMirrorStartAsync(_cts.Token);
            var inputSendTask = RunInputSendLoopAsync(_cts.Token);
            await Task.WhenAll(receiveTask, inputSendTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (State is not MirrorState.Stopped and not MirrorState.Error)
            {
                AirBridge.Core.AppLog.Info($"[Mirror:{SessionId}] Cancelled → Stopped");
                State = MirrorState.Stopped;
            }
        }
        catch (Exception ex)
        {
            AirBridge.Core.AppLog.Error($"[Mirror:{SessionId}] Unhandled exception → Error", ex);
            State = MirrorState.Error;
            throw;
        }
        finally
        {
            _inputQueue.Writer.TryComplete();
            CleanUp();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        _inputQueue.Writer.TryComplete();
        _cts?.Cancel();

        try
        {
            await _channel.SendAsync(
                new ProtocolMessage(MessageType.MirrorStop, Array.Empty<byte>()))
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort; channel may already be closed
        }

        // Null out _window before closing so CleanUp() in the StartAsync finally block
        // cannot attempt a second close on an already-closed WinUI 3 window (which crashes).
        var win = _window;
        _window = null;
        win?.Close();
        State = MirrorState.Stopped;
    }

    /// <summary>
    /// Enqueues an input event for forwarding to the connected Android device.
    /// Non-blocking: if the queue is full the oldest pending event is dropped.
    /// </summary>
    public Task SendInputAsync(
        InputEventArgs inputEvent,
        CancellationToken cancellationToken = default)
    {
        if (inputEvent is null) return Task.CompletedTask;
        _inputQueue.Writer.TryWrite(inputEvent);
        return Task.CompletedTask;
    }

    // ── Inbound message delivery ──────────────────────────────────────────

    /// <summary>
    /// Returns a handler delegate that delivers an incoming <see cref="ProtocolMessage"/>
    /// into this session's internal queue.  Register this with
    /// <c>DeviceConnectionService.AddMessageHandler</c> so that
    /// <see cref="AirBridge.App.Services.DeviceConnectionService"/> — the sole
    /// <see cref="IMessageChannel"/> reader — routes mirror-related messages here instead
    /// of reading the channel a second time (which would throw
    /// <see cref="NotSupportedException"/> on <see cref="System.Net.Security.SslStream"/>).
    /// </summary>
    public Func<ProtocolMessage, Task> CreateMessageHandler() =>
        msg =>
        {
            _inboundQueue.Writer.TryWrite(msg);
            return Task.CompletedTask;
        };

    // ── Core receive / message pump ────────────────────────────────────────

    private async Task HandleMirrorStartAsync(CancellationToken ct)
    {
        // Announce the mirror session to Android — only when Windows initiated.
        // When Android initiated (user tapped mirror on phone), Android already knows it's
        // mirroring so we must NOT echo MirrorStart back or it will confuse the protocol.
        if (!_androidInitiated)
        {
            AirBridge.Core.AppLog.Info($"[Mirror:{SessionId}] Connecting → sending MirrorStart to Android");
            await _channel.SendAsync(
                new ProtocolMessage(MessageType.MirrorStart, Array.Empty<byte>()), ct)
                .ConfigureAwait(false);
        }
        else
        {
            AirBridge.Core.AppLog.Info($"[Mirror:{SessionId}] Connecting → Android-initiated, skipping MirrorStart send");
        }

        // Create decoder (headless-safe: decoder can exist without a window)
        if (_decoderFactory is not null)
        {
            _decoder = _decoderFactory();

            // Initialize the decoder with the stream resolution before any frames arrive.
            // _width/_height come from the MirrorStart payload (Android-initiated) or
            // fall back to 1080×2340 (Windows-initiated, Android will match its capture size).
            await _decoder.InitializeAsync(_width, _height).ConfigureAwait(false);

            if (_windowFactory is not null)
            {
                _window = _windowFactory(_decoder);

                // Wire input relay: window raises events → enqueue for send loop
                _window.InputEventRaised += (_, evt) => _inputQueue.Writer.TryWrite(evt);

                // Wire drag-and-drop callback
                _window.OnFilesDropped = async files =>
                {
                    foreach (var file in files)
                        await SendFileAsync(file, _cts?.Token ?? default).ConfigureAwait(false);
                };

                // Attach the decoder's MediaStreamSource to the window's video surface
                // so WMF begins pulling NAL units and decoded frames appear on screen.
                _window.AttachDecoder(_decoder);

                // Open at phone resolution (scaled to fit screen), always-on-top.
                _window.Open(_width, _height);
            }
        }

        AirBridge.Core.AppLog.Info($"[Mirror:{SessionId}] Connecting → Active (decoder={(  _decoder is not null ? "yes" : "headless")}, window={(_window is not null ? "yes" : "none")})");
        State = MirrorState.Active;

        // Request a fresh IDR keyframe from Android.  The first IDR likely arrived before
        // our decode pipeline was fully set up (~1s of window/decoder init) and was dropped.
        // Sending MirrorStart signals Android to force a sync frame immediately.
        if (_androidInitiated)
        {
            try
            {
                await _channel.SendAsync(
                    new ProtocolMessage(MessageType.MirrorStart, Array.Empty<byte>()), ct)
                    .ConfigureAwait(false);
                AirBridge.Core.AppLog.Info($"[Mirror:{SessionId}] Sent MirrorStart → requested IDR from Android");
            }
            catch { /* best-effort */ }
        }

        // Message pump — reads from the internal queue, NOT from _channel.ReceiveAsync().
        // DeviceConnectionService is the sole channel reader; it dispatches messages here
        // via the handler returned by CreateMessageHandler().
        await foreach (var msg in _inboundQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            switch (msg.Type)
            {
                case MessageType.MirrorStart:
                    // Android echoes MirrorStart back when it receives ours (Windows-initiated).
                    // Ignore it here — DeviceConnectionService raises AndroidMirrorStartRequested
                    // separately; we must not act on it a second time inside the running session.
                    AirBridge.Core.AppLog.Info($"[Mirror:{SessionId}] Ignoring MirrorStart echo from Android (already active)");
                    break;

                case MessageType.MirrorFrame:
                    AirBridge.Core.AppLog.Debug($"[Mirror:{SessionId}] MirrorFrame payload={msg.Payload.Length}B");
                    if (_decoder is not null)
                    {
                        var frame = MirrorFrameMessage.FromBytes(msg.Payload);
                        await _decoder.PushFrameAsync(frame.NalData, frame.IsKeyFrame, frame.PresentationTimestampUs, ct).ConfigureAwait(false);
                    }
                    break;

                case MessageType.MirrorStop:
                    AirBridge.Core.AppLog.Info($"[Mirror:{SessionId}] Active → Stopped (MirrorStop received)");
                    _window?.Close();
                    State = MirrorState.Stopped;
                    _cts?.Cancel(); // unblock RunInputSendLoopAsync
                    return;

                // ACKs from the Android transfer receiver — forwarded, not consumed here
                case MessageType.FileTransferAck:
                case MessageType.FileTransferEnd:
                    break;
            }
        }

        AirBridge.Core.AppLog.Info($"[Mirror:{SessionId}] Message pump exited → Stopped");
        _window?.Close();
        State = MirrorState.Stopped;
        _cts?.Cancel(); // unblock RunInputSendLoopAsync so Task.WhenAll can complete
    }

    // ── Input send loop ────────────────────────────────────────────────────

    private async Task RunInputSendLoopAsync(CancellationToken ct)
    {
        await foreach (var evt in _inputQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
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
                    new ProtocolMessage(MessageType.InputEvent, msg.ToBytes()), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { break; /* Channel dead — stop the loop */ }
        }
    }

    // ── File transfer ──────────────────────────────────────────────────────

    /// <summary>
    /// Sends a dropped file to the connected Android device using the injected
    /// <see cref="ITransferEngine"/>. No-op when no engine is injected.
    /// </summary>
    public async Task SendFileAsync(IDroppedFile file, CancellationToken ct)
    {
        if (_transferEngine is null) return;

        await using var adapter = new ChannelNetworkStreamAdapter(_channel, ct);
        var result = await _transferEngine.SendFileAsync(
            file.Path, adapter, _transferProgress, ct).ConfigureAwait(false);
        _ = result;
    }

    // ── Cleanup ────────────────────────────────────────────────────────────

    private void CleanUp()
    {
        // Complete the inbound queue so ReadAllAsync exits if it is still running.
        _inboundQueue.Writer.TryComplete();
        try { _window?.Close(); }   catch { }
        try { _decoder?.Dispose(); } catch { }
        _decoder = null;
        _window  = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _inputQueue.Writer.TryComplete();
        _cts?.Cancel();
        _cts?.Dispose();
        CleanUp();
    }
}
