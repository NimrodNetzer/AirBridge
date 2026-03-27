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

    // ── Runtime state ──────────────────────────────────────────────────────

    private MirrorState         _state = MirrorState.Connecting;
    private CancellationTokenSource? _cts;
    private IMirrorWindowHost?  _window;
    private IMirrorDecoder?     _decoder;
    private readonly object     _stateLock = new();

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
        IProgress<long>? transferProgress = null)
    {
        SessionId         = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _channel          = channel   ?? throw new ArgumentNullException(nameof(channel));
        Mode              = MirrorMode.PhoneWindow;
        _decoderFactory   = decoderFactory;
        _windowFactory    = windowFactory;
        _transferEngine   = transferEngine;
        _transferProgress = transferProgress;
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
                State = MirrorState.Stopped;
        }
        catch
        {
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

        _window?.Close();
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

    // ── Core receive / message pump ────────────────────────────────────────

    private async Task HandleMirrorStartAsync(CancellationToken ct)
    {
        // Announce the mirror session to Android
        await _channel.SendAsync(
            new ProtocolMessage(MessageType.MirrorStart, Array.Empty<byte>()), ct)
            .ConfigureAwait(false);

        // Create decoder (headless-safe: decoder can exist without a window)
        if (_decoderFactory is not null)
        {
            _decoder = _decoderFactory();

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

                _window.Show();
            }
        }

        State = MirrorState.Active;

        // Message pump
        while (!ct.IsCancellationRequested)
        {
            var msg = await _channel.ReceiveAsync(ct).ConfigureAwait(false);
            if (msg is null) break; // channel closed cleanly

            switch (msg.Type)
            {
                case MessageType.MirrorFrame:
                    if (_decoder is not null)
                        await _decoder.PushFrameAsync(msg.Payload, ct).ConfigureAwait(false);
                    break;

                case MessageType.MirrorStop:
                    _window?.Close();
                    State = MirrorState.Stopped;
                    return;

                // ACKs from the Android transfer receiver — forwarded, not consumed here
                case MessageType.FileTransferAck:
                case MessageType.FileTransferEnd:
                    break;

                default:
                    break;
            }
        }

        _window?.Close();
        State = MirrorState.Stopped;
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
            catch { /* Best-effort: drop the event if the channel is gone */ }
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
