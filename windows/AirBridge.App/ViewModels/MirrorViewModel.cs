using AirBridge.App.Services;
using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Mirror;
using AirBridge.Mirror.Interfaces;
using AirBridge.Transport.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace AirBridge.App.ViewModels;

/// <summary>
/// ViewModel for the Mirror page. Manages phone-window and tablet-display sessions.
/// </summary>
public sealed partial class MirrorViewModel : ObservableObject, IDisposable
{
    private readonly MirrorServiceImpl _mirrorService;
    private readonly DeviceConnectionService _connection;
    private readonly IDeviceRegistry _registry;
    private readonly DispatcherQueue _dispatcher;

    private IMirrorSession? _activeSession;
    private IMessageChannel? _channel;
    private DeviceInfo? _connectedDevice;

    [ObservableProperty]
    private bool _isMirroring;

    [ObservableProperty]
    private MirrorMode _mirrorMode = MirrorMode.PhoneWindow;

    [ObservableProperty]
    private string _statusMessage = "No device connected";

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>
    /// Initialises the ViewModel and subscribes to connection lifecycle events so
    /// <see cref="IsConnected"/> tracks the active session automatically.
    /// </summary>
    public MirrorViewModel(IMirrorService mirrorService, DeviceConnectionService connection, IDeviceRegistry registry)
    {
        // MirrorServiceImpl exposes StartMirrorWithChannelAsync which we need.
        _mirrorService = (MirrorServiceImpl)mirrorService;
        _connection    = connection;
        _registry      = registry;
        _dispatcher    = DispatcherQueue.GetForCurrentThread();

        _connection.DeviceConnected    += OnDeviceConnected;
        _connection.DeviceDisconnected += OnDeviceDisconnected;

        // Reflect any session that was already active before this ViewModel was created.
        var existingId = _connection.ConnectedDeviceIds.FirstOrDefault();
        if (existingId is not null) ActivateDevice(existingId);
    }

    private void OnDeviceConnected(object? sender, string deviceId)
        => _dispatcher.TryEnqueue(() => ActivateDevice(deviceId));

    private void OnDeviceDisconnected(object? sender, string deviceId)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_connectedDevice?.DeviceId != deviceId) return;
            _channel         = null;
            _connectedDevice = null;
            IsConnected      = false;
            StatusMessage    = "Device disconnected";
        });
    }

    private void ActivateDevice(string deviceId)
    {
        var channel = _connection.GetActiveSession(deviceId);
        if (channel is null) return;
        var device = _registry.GetAllDevices().FirstOrDefault(d => d.DeviceId == deviceId)
                     ?? new DeviceInfo(deviceId, deviceId, DeviceType.AndroidPhone, string.Empty, 0, false);
        _channel         = channel;
        _connectedDevice = device;
        IsConnected      = true;
        StatusMessage    = $"Connected to {device.DeviceName}";
    }

    /// <summary>Starts a phone-window mirror session.</summary>
    [RelayCommand(CanExecute = nameof(CanStartSession))]
    private async Task StartPhoneWindowAsync()
    {
        await StartSessionAsync(MirrorMode.PhoneWindow);
    }

    /// <summary>Starts a tablet-display mirror session.</summary>
    [RelayCommand(CanExecute = nameof(CanStartSession))]
    private async Task StartTabletDisplayAsync()
    {
        await StartSessionAsync(MirrorMode.TabletDisplay);
    }

    private bool CanStartSession() => IsConnected && !IsMirroring;

    private async Task StartSessionAsync(MirrorMode mode)
    {
        if (_channel is null) return;

        try
        {
            IsMirroring   = true;
            MirrorMode    = mode;
            StatusMessage = "Connecting\u2026";

            _activeSession = await _mirrorService.StartMirrorWithChannelAsync(
                _channel, mode, CancellationToken.None);

            _activeSession.StateChanged += OnSessionStateChanged;
            StatusMessage = "Active";
        }
        catch (Exception ex)
        {
            IsMirroring   = false;
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    /// <summary>Stops the active mirror session.</summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        if (_activeSession is null) return;
        try
        {
            await _activeSession.StopAsync();
        }
        catch { /* best-effort */ }
        finally
        {
            _activeSession.StateChanged -= OnSessionStateChanged;
            _activeSession.Dispose();
            _activeSession = null;
            IsMirroring    = false;
            StatusMessage  = "Session stopped";
        }
    }

    private void OnSessionStateChanged(object? sender, MirrorState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            StatusMessage = state switch
            {
                MirrorState.Connecting => "Connecting\u2026",
                MirrorState.Active     => "Active",
                MirrorState.Paused     => "Paused",
                MirrorState.Stopped    => "Stopped",
                MirrorState.Error      => "Error",
                _                      => state.ToString()
            };

            if (state is MirrorState.Stopped or MirrorState.Error)
                IsMirroring = false;
        });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _connection.DeviceConnected    -= OnDeviceConnected;
        _connection.DeviceDisconnected -= OnDeviceDisconnected;
        _activeSession?.Dispose();
    }
}
