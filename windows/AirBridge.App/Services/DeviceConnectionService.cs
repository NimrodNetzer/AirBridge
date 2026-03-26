using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Core.Pairing;
using AirBridge.Transport.Interfaces;
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

    /// <summary>Live list of devices seen on the LAN, updated on the UI thread.</summary>
    public ObservableCollection<DeviceInfo> DiscoveredDevices { get; } = new();

    /// <summary>Raised when a pairing PIN is ready to be shown to the user.</summary>
    public event EventHandler<string>? PairingPinReady;

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
    }

    /// <summary>Starts mDNS discovery. Safe to call multiple times.</summary>
    public async Task StartDiscoveryAsync()
    {
        _discoveryCts?.Cancel();
        _discoveryCts = new CancellationTokenSource();
        await _discovery.StartAsync(_discoveryCts.Token).ConfigureAwait(false);
    }

    /// <summary>Stops mDNS discovery.</summary>
    public async Task StopDiscoveryAsync()
    {
        _discoveryCts?.Cancel();
        await _discovery.StopAsync().ConfigureAwait(false);
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
    }
}
