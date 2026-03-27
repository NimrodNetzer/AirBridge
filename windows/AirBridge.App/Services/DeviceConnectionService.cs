using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Core.Pairing;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace AirBridge.App.Services;

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
    private CancellationTokenSource? _discoveryCts;
    private IMessageChannel? _pendingPairingChannel;
    private byte[]? _pendingRemoteKey;
    private string? _pendingRemoteDeviceId;
    private string? _pendingPin;

    // ── Active session tracking ─────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, IMessageChannel> _activeSessions = new();

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
        IDeviceRegistry registry)
    {
        _discovery         = discovery;
        _connectionManager = connectionManager;
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
    /// Connects to an already-paired <paramref name="device"/> without running the pairing
    /// handshake. Stores the resulting channel as the active session and begins monitoring for
    /// disconnection.
    /// </summary>
    public async Task ConnectToPairedDeviceAsync(DeviceInfo device, CancellationToken ct)
    {
        var channel = await _connectionManager.ConnectAsync(device, ct).ConfigureAwait(false);
        RegisterSession(device.DeviceId, channel);
    }

    // ── Session lifecycle helpers ───────────────────────────────────────────

    /// <summary>
    /// Registers <paramref name="channel"/> as the active session for <paramref name="deviceId"/>,
    /// fires <see cref="DeviceConnected"/>, and starts a background task that fires
    /// <see cref="DeviceDisconnected"/> when the channel closes.
    /// </summary>
    public void RegisterSession(string deviceId, IMessageChannel channel)
    {
        // Replace any stale session for this device.
        _activeSessions[deviceId] = channel;
        DeviceConnected?.Invoke(this, deviceId);

        // Monitor for disconnect on a background task.
        _ = MonitorSessionAsync(deviceId, channel);
    }

    private async Task MonitorSessionAsync(string deviceId, IMessageChannel channel)
    {
        try
        {
            // Drain incoming messages until the channel closes cleanly (null) or throws.
            while (true)
            {
                var msg = await channel.ReceiveAsync().ConfigureAwait(false);
                if (msg is null)
                    break; // clean close
                // Messages received here are non-pairing messages (e.g. Ping/Pong).
                // Feature services (transfer, mirror) should register their own receive
                // loop on the channel; this loop just keeps the session alive.
            }
        }
        catch
        {
            // Transport error — treat as disconnect.
        }
        finally
        {
            // Only remove if this is still the registered session for the device
            // (a reconnect may have replaced it).
            _activeSessions.TryRemove(new KeyValuePair<string, IMessageChannel>(deviceId, channel));
            DeviceDisconnected?.Invoke(this, deviceId);
        }
    }

    // ── Inbound connection handler ──────────────────────────────────────────

    private async void OnConnectionReceived(object? sender, IMessageChannel channel)
    {
        try
        {
            // Give the remote side up to 30 s to send the opening PAIRING_REQUEST.
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

            // Already-paired device reconnecting — register immediately as active session.
            if (msg.Type != MessageType.PairingRequest)
            {
                RegisterSession(channel.RemoteDeviceId, channel);
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
                DeviceType: AirBridge.Core.Models.DeviceType.AndroidPhone,
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
        _registry.AddOrUpdate(device);
        DispatchToUiThread(() => DiscoveredDevices.Add(device));
    }

    private void OnDeviceLost(object? sender, string deviceId)
    {
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
