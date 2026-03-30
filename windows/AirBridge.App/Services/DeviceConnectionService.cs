using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Mirror;
using AirBridge.Core.Pairing;
using AirBridge.Transfer.Interfaces;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace AirBridge.App.Services;

/// <summary>Event args for <see cref="DeviceConnectionService.AndroidMirrorStartRequested"/>.</summary>
public sealed record AndroidMirrorStartArgs(string DeviceId, int Width, int Height);

/// <summary>
/// Orchestrates device discovery, connection establishment, and pairing
/// for the UI layer. Provides the single point of truth for discovered
/// devices and exposes events consumed by ViewModels.
/// </summary>
public sealed class DeviceConnectionService : IDisposable
{
    private readonly IDiscoveryService _discovery;
    private readonly IConnectionManager _connectionManager;
    private readonly PairingService _pairing;
    private readonly IDeviceRegistry _registry;
    private readonly IFileTransferService _fileTransfer;
    private CancellationTokenSource? _discoveryCts;
    private IMessageChannel? _pendingPairingChannel;
    private byte[]? _pendingRemoteKey;
    private string? _pendingRemoteDeviceId;
    private string? _pendingPin;

    // ── Active session tracking ─────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, IMessageChannel> _activeSessions = new();

    // ── Message dispatcher ──────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, List<Func<ProtocolMessage, Task>>> _messageHandlers = new();

    /// <summary>
    /// Registers a handler that will be invoked for every incoming message on the active session
    /// for <paramref name="deviceId"/>. Multiple handlers may be registered per device; all are
    /// called in registration order for each message.
    /// </summary>
    public void AddMessageHandler(string deviceId, Func<ProtocolMessage, Task> handler)
    {
        _messageHandlers.AddOrUpdate(
            deviceId,
            _ => new List<Func<ProtocolMessage, Task>> { handler },
            (_, existing) => { lock (existing) { existing.Add(handler); return existing; } });
    }

    /// <summary>Removes all message handlers for <paramref name="deviceId"/>.</summary>
    public void RemoveMessageHandlers(string deviceId)
        => _messageHandlers.TryRemove(deviceId, out _);

    /// <summary>
    /// Removes a single previously-registered handler for <paramref name="deviceId"/>.
    /// Use this to unregister a mirror handler without disturbing the file-transfer handler.
    /// </summary>
    public void RemoveMessageHandler(string deviceId, Func<ProtocolMessage, Task> handler)
    {
        if (_messageHandlers.TryGetValue(deviceId, out var handlers))
            lock (handlers) { handlers.Remove(handler); }
    }

    /// <summary>
    /// Returns the active <see cref="IMessageChannel"/> for <paramref name="deviceId"/>,
    /// or <see langword="null"/> if no session is currently open for that device.
    /// </summary>
    public IMessageChannel? GetActiveSession(string deviceId)
        => _activeSessions.TryGetValue(deviceId, out var ch) ? ch : null;

    /// <summary>The set of device IDs that currently have an open session.</summary>
    public IReadOnlyCollection<string> ConnectedDeviceIds => _activeSessions.Keys.ToList().AsReadOnly();

    /// <summary>Raised with the device ID when a new active session is established.</summary>
    public event EventHandler<string>? DeviceConnected;

    /// <summary>Raised with the device ID when an active session is closed or drops.</summary>
    public event EventHandler<string>? DeviceDisconnected;

    /// <summary>
    /// Raised when Android sends a <see cref="MessageType.MirrorStart"/> to Windows,
    /// indicating that the user tapped "mirror" on the Android device.
    /// The args carry the device ID and the screen dimensions reported by Android.
    /// Windows should start a mirror session in response without sending
    /// <see cref="MessageType.MirrorStart"/> back to Android.
    /// </summary>
    public event EventHandler<AndroidMirrorStartArgs>? AndroidMirrorStartRequested;

    // ── Existing fields ─────────────────────────────────────────────────────

    /// <summary>PIN from a pending inbound pairing request, or null if none.</summary>
    public string? PendingPairingPin => _pendingPin;

    /// <summary>Live list of devices seen on the LAN, updated on the UI thread.</summary>
    public ObservableCollection<DeviceInfo> DiscoveredDevices { get; } = new();

    /// <summary>Raised when a pairing PIN is ready to be shown to the user.</summary>
    public event EventHandler<string>? PairingPinReady;

    /// <summary>Raised when an Android device initiates a pairing request. UI should show the PIN.</summary>
    public event EventHandler<(string Pin, string DeviceId)>? IncomingPairingRequest;

    public DeviceConnectionService(
        IDiscoveryService discovery,
        IConnectionManager connectionManager,
        IPairingService pairing,
        IDeviceRegistry registry,
        IFileTransferService fileTransfer)
    {
        _discovery         = discovery;
        _connectionManager = connectionManager;
        _fileTransfer      = fileTransfer;
        // PairingService exposes PinGenerated which IPairingService does not —
        // cast is safe because App.cs registers PairingService as the concrete type.
        _pairing  = (PairingService)pairing;
        _registry = registry;

        _discovery.DeviceFound += OnDeviceFound;
        _discovery.DeviceLost  += OnDeviceLost;

        // Forward PIN-ready event from the core service
        _pairing.PinGenerated += (_, pin) => PairingPinReady?.Invoke(this, pin);

        _connectionManager.ConnectionReceived += OnConnectionReceived;
    }

    /// <summary>Starts mDNS discovery and begins accepting incoming TLS connections.</summary>
    public async Task StartDiscoveryAsync()
    {
        _discoveryCts?.Cancel();
        _discoveryCts = new CancellationTokenSource();
        await _connectionManager.StartListeningAsync(_discoveryCts.Token).ConfigureAwait(false);
        await _discovery.StartAsync(_discoveryCts.Token).ConfigureAwait(false);
    }

    /// <summary>Stops mDNS discovery and the TLS listener.</summary>
    public async Task StopDiscoveryAsync()
    {
        _discoveryCts?.Cancel();
        await _discovery.StopAsync().ConfigureAwait(false);
        await _connectionManager.StopAsync().ConfigureAwait(false);
    }

    /// <summary>Opens a TLS connection to <paramref name="device"/>.</summary>
    public Task<IMessageChannel> ConnectToDeviceAsync(DeviceInfo device, CancellationToken ct)
        => _connectionManager.ConnectAsync(device, ct);

    /// <summary>Returns true if <paramref name="deviceId"/> is already paired.</summary>
    public bool IsPaired(string deviceId) => _pairing.IsPaired(deviceId);

    /// <summary>
    /// Initiates or accepts pairing with <paramref name="device"/> on
    /// <paramref name="channel"/>. Fires <see cref="PairingPinReady"/> when the
    /// 6-digit PIN is available.
    /// </summary>
    public async Task<bool> PairAsync(DeviceInfo device, IMessageChannel channel, CancellationToken ct)
    {
        var result = await _pairing.RequestPairingAsync(device, ct).ConfigureAwait(false);
        if (result == AirBridge.Core.Interfaces.PairingResult.Success ||
            result == AirBridge.Core.Interfaces.PairingResult.AlreadyPaired)
        {
            // Mark device as paired in registry
            var paired = device with { IsPaired = true };
            _registry.AddOrUpdate(paired);

            // Keep the channel alive as the active session for this device.
            RegisterSession(device.DeviceId, channel);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Connects to an already-paired <paramref name="device"/> and keeps the connection alive
    /// indefinitely — until <paramref name="ct"/> is cancelled (i.e. the user explicitly
    /// disconnects or closes the app).
    /// <para>
    /// The loop never gives up on its own:
    /// <list type="bullet">
    ///   <item>A 15-second connect timeout prevents a single attempt from hanging.</item>
    ///   <item>On failure, exponential back-off grows from 2 s → 60 s, then stays at 60 s.</item>
    ///   <item>On a successful connection that later drops, back-off resets to 2 s.</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task ConnectToPairedDeviceAsync(DeviceInfo device, CancellationToken ct)
    {
        var backoffMs = 2_000;
        AppLog.Info($"[ConnSvc] Starting persistent connect loop for {device.DeviceId}");

        while (!ct.IsCancellationRequested)
        {
            IMessageChannel channel;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(15_000);
                channel = await _connectionManager.ConnectAsync(device, connectCts.Token)
                                                  .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // User cancelled — stop reconnecting.
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[ConnSvc] Connect to {device.DeviceId} failed: {ex.Message}; retry in {backoffMs}ms");
                try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                backoffMs = Math.Min(backoffMs * 2, 60_000);
                continue;
            }

            // Connected — register session and watch for disconnect.
            backoffMs = 2_000; // reset on success
            AppLog.Info($"[ConnSvc] Connected to {device.DeviceId}; registering session");
            RegisterSession(device.DeviceId, channel);

            var disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDisconnect(object? s, string id)
            {
                if (id == device.DeviceId) disconnectTcs.TrySetResult(true);
            }
            DeviceDisconnected += OnDisconnect;
            try
            {
                await disconnectTcs.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // User cancelled — stop reconnecting.
            }
            finally
            {
                DeviceDisconnected -= OnDisconnect;
            }

            AppLog.Info($"[ConnSvc] Session dropped for {device.DeviceId}; reconnecting immediately");
        }
    }

    // ── Session lifecycle helpers ───────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="channel"/> as the active session for <paramref name="deviceId"/>,
    /// fires <see cref="DeviceConnected"/>, and starts a background task that fires
    /// <see cref="DeviceDisconnected"/> when the channel closes.
    /// </summary>
    public void RegisterSession(string deviceId, IMessageChannel channel)
    {
        // If there is already a healthy session for this device, closing it would abort an
        // in-flight transfer.  Instead, close the new duplicate and keep the live session.
        // This handles the race where both ConnectToPairedDeviceAsync (outbound) and
        // OnConnectionReceived (inbound) succeed within the same reconnect window.
        if (_activeSessions.TryGetValue(deviceId, out var existingChannel) &&
            !ReferenceEquals(existingChannel, channel) &&
            existingChannel.IsConnected)
        {
            AppLog.Warn($"[ConnSvc] RegisterSession: duplicate for {deviceId} — existing session still alive; closing new duplicate");
            _ = channel.DisposeAsync().AsTask();
            return;
        }

        // Close any existing session for this device before replacing it.
        // This prevents two MonitorSessionAsync loops from racing on the same device ID,
        // which would cause one to steal PONG frames intended for the other's keepalive check.
        if (_activeSessions.TryRemove(deviceId, out var oldChannel) &&
            !ReferenceEquals(oldChannel, channel))
        {
            AppLog.Info($"RegisterSession: closing stale session for {deviceId}");
            _ = oldChannel.DisposeAsync().AsTask();
        }

        _activeSessions[deviceId] = channel;
        AppLog.Info($"Session registered: {deviceId}");

        // Clear stale handlers and re-register fresh ones for this channel.
        _messageHandlers.TryRemove(deviceId, out _);
        _fileTransfer.SetChannel(channel);
        AddMessageHandler(deviceId, _fileTransfer.CreateReceiveHandler());

        DeviceConnected?.Invoke(this, deviceId);

        // Monitor for disconnect on a background task.
        _ = MonitorSessionAsync(deviceId, channel);
    }

    private async Task MonitorSessionAsync(string deviceId, IMessageChannel channel)
    {
        try
        {
            while (true)
            {
                var msg = await channel.ReceiveAsync().ConfigureAwait(false);
                if (msg is null)
                {
                    AppLog.Info($"Channel closed cleanly: {deviceId}");
                    break;
                }

                AppLog.Info($"RX [{deviceId}] type={msg.Type} len={msg.Payload?.Length ?? 0}");

                // Android-initiated mirror: raise event so MirrorViewModel can start a session.
                // The MirrorStart is also forwarded to any registered handlers below so that
                // if a session handler is already registered it can process it too.
                if (msg.Type == MessageType.MirrorStart)
                {
                    int w = 0, h = 0;
                    if (msg.Payload?.Length >= 7)
                    {
                        var parsed = AirBridge.Mirror.MirrorStartMessage.FromBytes(msg.Payload);
                        w = parsed.Width;
                        h = parsed.Height;
                    }
                    AndroidMirrorStartRequested?.Invoke(this, new AndroidMirrorStartArgs(deviceId, w, h));
                }

                // Dispatch to all registered handlers for this device.
                if (_messageHandlers.TryGetValue(deviceId, out var handlers))
                {
                    List<Func<ProtocolMessage, Task>> snapshot;
                    lock (handlers) { snapshot = handlers.ToList(); }
                    foreach (var handler in snapshot)
                    {
                        try { await handler(msg).ConfigureAwait(false); }
                        catch (Exception hEx)
                        {
                            AppLog.Error($"Message handler threw for device {deviceId}, type {msg.Type}", hEx);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Transport error — treat as disconnect.
            AppLog.Error($"Transport error on session {deviceId}", ex);
        }
        finally
        {
            // Only clean up and raise DeviceDisconnected if this channel is still the active
            // session for the device. If RegisterSession() was called with a newer channel
            // while this one was draining, TryRemove(KVP) returns false — we must not touch
            // the new session's handlers or fire a spurious disconnect event.
            var wasActive = _activeSessions.TryRemove(
                new KeyValuePair<string, IMessageChannel>(deviceId, channel));

            _fileTransfer.ClearChannel(channel);

            if (wasActive)
            {
                _messageHandlers.TryRemove(deviceId, out _);
                AppLog.Info($"Session closed: {deviceId}");
                DeviceDisconnected?.Invoke(this, deviceId);
            }
            else
            {
                AppLog.Info($"Session closed (superseded by newer channel): {deviceId}");
            }
        }
    }

    // ── Inbound connection handler ──────────────────────────────────────────

    private async void OnConnectionReceived(object? sender, IMessageChannel channel)
    {
        try
        {
            // RemoteDeviceId is set from the HANDSHAKE exchange that occurs before this
            // event fires.  If this device is already paired, register the session
            // immediately — Android does not send a first application message on reconnect.
            var isPaired     = _pairing.IsPaired(channel.RemoteDeviceId);
            var hasSession   = _activeSessions.ContainsKey(channel.RemoteDeviceId);
            AppLog.Info($"[ConnSvc] Inbound: remoteId={channel.RemoteDeviceId} isPaired={isPaired} hasSession={hasSession}");

            if (isPaired)
            {
                AppLog.Info($"Reconnect from known device {channel.RemoteDeviceId} — registering immediately");
                RegisterSession(channel.RemoteDeviceId, channel);
                return;
            }

            // Unknown device — wait up to 30 s for a PAIRING_REQUEST.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            ProtocolMessage? msg;
            try
            {
                msg = await channel.ReceiveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Remote side did not send a request in time — drop the connection.
                await channel.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (msg is null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
                return;
            }

            // Unexpected non-pairing message from an unknown device — drop.
            if (msg.Type != MessageType.PairingRequest)
            {
                AppLog.Warn($"Non-pairing first message (type={msg.Type}) from unknown device {channel.RemoteDeviceId} — dropping");
                await channel.DisposeAsync().ConfigureAwait(false);
                return;
            }

            byte[] remoteKey;
            string pin;
            try
            {
                (remoteKey, pin) = AirBridge.Transport.Pairing.PairingCoordinator.ParseRequestPayload(msg.Payload);
            }
            catch
            {
                // Malformed payload — drop silently.
                await channel.DisposeAsync().ConfigureAwait(false);
                return;
            }

            // Use remote endpoint as temporary device ID until handshake completes.
            var remoteId = channel.RemoteDeviceId;

            _pendingPairingChannel   = channel;
            _pendingRemoteKey        = remoteKey;
            _pendingRemoteDeviceId   = remoteId;
            _pendingPin              = pin;

            _pairing.RaisePinGenerated(pin);
            IncomingPairingRequest?.Invoke(this, (pin, remoteId));
        }
        catch
        {
            // ignore — bad connection attempt; dispose channel best-effort
            try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Sends a PAIRING_RESPONSE on the pending inbound channel.
    /// </summary>
    /// <param name="accepted">
    /// <see langword="true"/> to accept the pairing request and persist the remote key;
    /// <see langword="false"/> to reject it.  In both cases the pending state is cleared.
    /// </param>
    /// <returns><see langword="true"/> when the response was sent successfully and (if
    /// accepting) the key was stored; <see langword="false"/> on any error or if there is
    /// no pending request.</returns>
    public async Task<bool> AcceptIncomingPairingAsync(bool accepted = true)
    {
        if (_pendingPairingChannel is null || _pendingRemoteKey is null || _pendingRemoteDeviceId is null)
            return false;

        // Capture pending state before clearing it so the finally block is safe.
        var channel  = _pendingPairingChannel;
        var remoteKey = _pendingRemoteKey;
        var remoteId  = _pendingRemoteDeviceId;

        try
        {
            // Wire format: [1 byte accepted][ushort key length][key bytes]
            // Matches Android PairingService.parseResponsePayload():
            //   dis.readBoolean() → dis.readUnsignedShort() → dis.readFully(key)
            var responsePayload = AirBridge.Transport.Pairing.PairingCoordinator.BuildResponsePayload(
                accepted:       accepted,
                localPublicKey: _pairing.GetLocalPublicKey());

            await channel.SendAsync(
                new ProtocolMessage(MessageType.PairingResponse, responsePayload))
                .ConfigureAwait(false);

            if (!accepted)
                return true; // rejection sent successfully

            await _pairing.StorePeerKeyAsync(remoteId, remoteKey).ConfigureAwait(false);

            var device = new AirBridge.Core.Models.DeviceInfo(
                DeviceId:   remoteId,
                DeviceName: remoteId,
                DeviceType: AirBridge.Core.Models.DeviceType.Unknown,
                IpAddress:  string.Empty,
                Port:       0,
                IsPaired:   true);
            _registry.AddOrUpdate(device);

            // Keep the channel alive as the active session.
            RegisterSession(remoteId, channel);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _pendingPairingChannel = null;
            _pendingRemoteKey      = null;
            _pendingRemoteDeviceId = null;
            _pendingPin            = null;
        }
    }

    // ── Discovery event handlers ────────────────────────────────────────────

    private void OnDeviceFound(object? sender, DeviceInfo device)
    {
        // Preserve persisted pairing state — mDNS discovery always has IsPaired=false.
        var entry = _pairing.IsPaired(device.DeviceId) ? device with { IsPaired = true } : device;
        _registry.AddOrUpdate(entry);
        DispatchToUiThread(() => DiscoveredDevices.Add(device));
    }

    private void OnDeviceLost(object? sender, string deviceId)
    {
        // Do not remove paired devices — pairing must survive mDNS advertisement expiry
        // so the device is still recognised as paired on reconnect.
        if (!_registry.IsPaired(deviceId))
            _registry.Remove(deviceId);

        DispatchToUiThread(() =>
        {
            var d = DiscoveredDevices.FirstOrDefault(x => x.DeviceId == deviceId);
            if (d is not null) DiscoveredDevices.Remove(d);
        });
    }

    private static void DispatchToUiThread(Action action)
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is not null)
            queue.TryEnqueue(() => action());
        else
            action(); // already on UI thread or test context
    }

    public void Dispose()
    {
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        _discovery.DeviceFound -= OnDeviceFound;
        _discovery.DeviceLost  -= OnDeviceLost;
        _connectionManager.ConnectionReceived -= OnConnectionReceived;
        _connectionManager.StopAsync().GetAwaiter().GetResult();
    }
}
