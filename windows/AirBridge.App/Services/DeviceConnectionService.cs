using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Core.Pairing;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
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
            return true;
        }
        return false;
    }

    // ── Inbound connection handler ──────────────────────────────────────────

    private async void OnConnectionReceived(object? sender, IMessageChannel channel)
    {
        try
        {
            // Read the first message to see what the remote side wants
            var msg = await channel.ReceiveAsync().ConfigureAwait(false);
            if (msg is null || msg.Type != MessageType.PairingRequest)
                return;

            var (remoteKey, pin) = AirBridge.Transport.Pairing.PairingCoordinator.ParseRequestPayload(msg.Payload);

            // Use remote endpoint as temporary device ID until handshake completes
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
            // ignore — bad connection attempt
        }
    }

    /// <summary>
    /// Sends a PAIRING_RESPONSE on the pending inbound channel to accept the request from Android.
    /// </summary>
    public async Task<bool> AcceptIncomingPairingAsync()
    {
        if (_pendingPairingChannel is null || _pendingRemoteKey is null || _pendingRemoteDeviceId is null)
            return false;

        try
        {
            var responsePayload = AirBridge.Transport.Pairing.PairingCoordinator.BuildResponsePayload(
                accepted: true,
                localPublicKey: _pairing.GetLocalPublicKey());

            await _pendingPairingChannel.SendAsync(
                new ProtocolMessage(MessageType.PairingResponse, responsePayload))
                .ConfigureAwait(false);

            await _pairing.StorePeerKeyAsync(_pendingRemoteDeviceId, _pendingRemoteKey)
                          .ConfigureAwait(false);

            var device = new AirBridge.Core.Models.DeviceInfo(
                DeviceId:   _pendingRemoteDeviceId,
                DeviceName: _pendingRemoteDeviceId,
                DeviceType: AirBridge.Core.Models.DeviceType.AndroidPhone,
                IpAddress:  string.Empty,
                Port:       0,
                IsPaired:   true);
            _registry.AddOrUpdate(device);

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
