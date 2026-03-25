using AirBridge.Core.Interfaces;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Mirror;

/// <summary>
/// Windows-side implementation of <see cref="IMirrorSession"/>.
/// <para>
/// Responsibilities:
/// <list type="bullet">
///   <item>Receives <see cref="MessageType.MirrorStart"/> from the Android device and
///         opens a floating window via the injected <see cref="IMirrorWindowHost"/>.</item>
///   <item>Feeds <see cref="MessageType.MirrorFrame"/> payloads to the
///         <see cref="IMirrorDecoder"/> and presents them in the window.</item>
///   <item>On <see cref="MessageType.MirrorStop"/>, closes the window and stops.</item>
///   <item>When files are dropped onto the window, calls <see cref="SendFileAsync"/>
///         which delegates to the injected <see cref="ITransferEngine"/>.</item>
///   <item>Passes through any <see cref="MessageType.FileTransferStart"/> /
///         <c>FileChunk</c> / <c>FileTransferEnd</c> messages to the Android
///         transfer receiver rather than consuming them.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MirrorSession : IMirrorSession
{
    // ── Dependencies ───────────────────────────────────────────────────────

    private readonly IMessageChannel             _channel;
    private readonly Func<IMirrorDecoder>?        _decoderFactory;
    private readonly Func<IMirrorDecoder, IMirrorWindowHost>? _windowFactory;
    private readonly ITransferEngine?             _transferEngine;
    private readonly IProgress<long>?             _transferProgress;

    // ── Runtime state ──────────────────────────────────────────────────────

    private MirrorState                          _state = MirrorState.Connecting;
    private CancellationTokenSource?             _cts;
    private IMirrorWindowHost?                   _window;
    private IMirrorDecoder?                      _decoder;
    private readonly object                      _stateLock = new();

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
    /// <param name="channel">
    ///   The transport channel to the paired Android device.
    ///   Must already be connected.
    /// </param>
    /// <param name="decoderFactory">
    ///   Factory that creates an <see cref="IMirrorDecoder"/> when the session starts.
    ///   Pass <c>null</c> for headless/test mode (frames are discarded).
    /// </param>
    /// <param name="windowFactory">
    ///   Factory that creates an <see cref="IMirrorWindowHost"/> given a decoder.
    ///   Pass <c>null</c> for headless/test mode.
    /// </param>
    /// <param name="transferEngine">
    ///   Engine used to send dropped files to the Android device.
    ///   Pass <c>null</c> to disable drag-and-drop file transfer
    ///   (drops will be silently ignored).
    /// </param>
    /// <param name="transferProgress">
    ///   Optional progress reporter for file transfers; receives bytes-transferred counts.
    /// </param>
    public MirrorSession(
        string sessionId,
        IMessageChannel channel,
        Func<IMirrorDecoder>? decoderFactory = null,
        Func<IMirrorDecoder, IMirrorWindowHost>? windowFactory = null,
        ITransferEngine? transferEngine = null,
        IProgress<long>? transferProgress = null)
    {
        SessionId        = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _channel         = channel   ?? throw new ArgumentNullException(nameof(channel));
        Mode             = MirrorMode.PhoneWindow;
        _decoderFactory  = decoderFactory;
        _windowFactory   = windowFactory;
        _transferEngine  = transferEngine;
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
            await HandleMirrorStartAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            State = MirrorState.Stopped;
        }
        catch
        {
            State = MirrorState.Error;
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        _cts?.Cancel();

        // Notify the Android side that we are stopping
        try
        {
            await _channel.SendAsync(
                new ProtocolMessage(MessageType.MirrorStop, Array.Empty<byte>()))
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort; the channel may already be closed
        }

        _window?.Close();
        State = MirrorState.Stopped;
    }

    /// <inheritdoc/>
    public async Task SendInputAsync(InputEventArgs inputEvent, CancellationToken cancellationToken = default)
    {
        // Input relay is implemented in Iteration 6 (full mirror).
        // For MVP the method exists to satisfy the interface but does nothing.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ── Core receive loop ──────────────────────────────────────────────────

    /// <summary>
    /// Sends a <see cref="MessageType.MirrorStart"/> request, waits for the Android
    /// device to acknowledge, creates the floating window, and then pumps
    /// incoming messages until the session is stopped.
    /// </summary>
    private async Task HandleMirrorStartAsync(CancellationToken ct)
    {
        // Send mirror-start request
        await _channel.SendAsync(
            new ProtocolMessage(MessageType.MirrorStart, Array.Empty<byte>()), ct)
            .ConfigureAwait(false);

        // Create decoder and window (may be null in headless/test mode)
        if (_decoderFactory is not null && _windowFactory is not null)
        {
            _decoder = _decoderFactory();
            _window  = _windowFactory(_decoder);

            // Wire drag-and-drop callback
            _window.OnFilesDropped = async files =>
            {
                foreach (var file in files)
                    await SendFileAsync(file, _cts?.Token ?? default).ConfigureAwait(false);
            };

            _window.Show();
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

                // File-transfer messages are forwarded: the Android side initiates
                // a receive session when the Windows side sends files.
                // These message types arrive as ACKs from the Android receiver.
                case MessageType.FileTransferAck:
                case MessageType.FileTransferEnd:
                    // Handled by the active TransferSession; we do not consume them here.
                    break;

                // All other message types are ignored to keep the mirror session
                // forward-compatible as the protocol evolves.
                default:
                    break;
            }
        }

        _window?.Close();
        State = MirrorState.Stopped;
    }

    // ── File transfer ──────────────────────────────────────────────────────

    /// <summary>
    /// Sends a dropped file to the connected Android device using the injected
    /// <see cref="ITransferEngine"/>.
    /// <para>
    /// If no transfer engine was injected the call is a silent no-op — this
    /// allows headless and test instantiation without file-transfer capability.
    /// </para>
    /// </summary>
    /// <param name="file">The file to send.</param>
    /// <param name="ct">Token that cancels the transfer in progress.</param>
    public async Task SendFileAsync(IDroppedFile file, CancellationToken ct)
    {
        if (_transferEngine is null) return;

        // The transfer protocol operates over a raw Stream.  We expose the
        // channel's underlying stream by routing through a MemoryStream bridge
        // and then sending it as a series of ProtocolMessage payloads.
        // For simplicity in this MVP, the transfer bytes are sent directly over
        // the channel stream via a NetworkStreamAdapter so that the Android
        // TransferSession receiver loop can consume them.
        await using var adapter = new ChannelNetworkStreamAdapter(_channel, ct);

        var result = await _transferEngine.SendFileAsync(
            file.Path,
            adapter,
            _transferProgress,
            ct).ConfigureAwait(false);

        // Result is informational; the session continues even if a transfer fails.
        // In a future iteration, progress/error could be surfaced to the UI.
        _ = result;
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _decoder?.Dispose();
    }
}
