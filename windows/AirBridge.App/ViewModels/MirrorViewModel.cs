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

    // Handler registered with DeviceConnectionService for the active mirror session.
    // Kept here so we can remove just this handler (not the file-transfer handler) on stop.
    private Func<AirBridge.Transport.Protocol.ProtocolMessage, Task>? _mirrorHandler;
    private string? _mirrorDeviceId;

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

        _connection.DeviceConnected          += OnDeviceConnected;
        _connection.DeviceDisconnected       += OnDeviceDisconnected;
        _connection.AndroidMirrorStartRequested += OnAndroidMirrorStartRequested;

        // Reflect any session that was already active before this ViewModel was created.
        var existingId = _connection.ConnectedDeviceIds.FirstOrDefault();
        if (existingId is not null) ActivateDevice(existingId);
    }

    private void OnDeviceConnected(object? sender, string deviceId)
        => _dispatcher.TryEnqueue(async () =>
        {
            ActivateDevice(deviceId);
            // iPad connects as receiver — auto-start tablet display so it gets the stream immediately.
            if (_connectedDevice?.DeviceType == DeviceType.iPad && !IsMirroring)
                await StartSessionAsync(MirrorMode.TabletDisplay);
        });

    private void OnAndroidMirrorStartRequested(object? sender, AirBridge.App.Services.AndroidMirrorStartArgs args)
        => _dispatcher.TryEnqueue(async () =>
        {
            // Ensure the device is activated in case DeviceConnected fired just before this.
            if (_connectedDevice is null || _connectedDevice.DeviceId != args.DeviceId)
                ActivateDevice(args.DeviceId);

            if (IsMirroring) return; // already mirroring
            await StartSessionAsync(MirrorMode.PhoneWindow, androidInitiated: true,
                                    width: args.Width, height: args.Height);
        });

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
                     ?? new DeviceInfo(deviceId, deviceId, DeviceType.Unknown, string.Empty, 0, false);
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

    private async Task StartSessionAsync(MirrorMode mode, bool androidInitiated = false,
                                          int width = 0, int height = 0)
    {
        if (_channel is null) return;

        try
        {
            IsMirroring   = true;
            MirrorMode    = mode;
            StatusMessage = "Connecting\u2026";

            _activeSession = await _mirrorService.StartMirrorWithChannelAsync(
                _channel, mode, CancellationToken.None, androidInitiated, width, height);

            _activeSession.StateChanged += OnSessionStateChanged;

            // Register the session's inbound message handler so DeviceConnectionService
            // (the sole channel reader) routes MirrorFrame / MirrorStop messages here,
            // avoiding a concurrent SslStream read which throws NotSupportedException.
            if (_connectedDevice is not null)
            {
                _mirrorDeviceId = _connectedDevice.DeviceId;
                if (_activeSession is AirBridge.Mirror.MirrorSession mirrorSession)
                    _mirrorHandler = mirrorSession.CreateMessageHandler();
                else if (_activeSession is AirBridge.Mirror.TabletDisplaySession tabletSession)
                    _mirrorHandler = tabletSession.CreateMessageHandler(
                        onStop: () => { _dispatcher.TryEnqueue(async () => await StopAsync()); return Task.CompletedTask; });
                if (_mirrorHandler is not null)
                    _connection.AddMessageHandler(_mirrorDeviceId, _mirrorHandler);
            }

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
            UnregisterMirrorHandler();
            _activeSession.Dispose();
            _activeSession = null;
            IsMirroring    = false;
            StatusMessage  = "Session stopped";
        }
    }

    private void UnregisterMirrorHandler()
    {
        if (_mirrorHandler is not null && _mirrorDeviceId is not null)
        {
            _connection.RemoveMessageHandler(_mirrorDeviceId, _mirrorHandler);
            _mirrorHandler  = null;
            _mirrorDeviceId = null;
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
        _connection.DeviceConnected             -= OnDeviceConnected;
        _connection.DeviceDisconnected          -= OnDeviceDisconnected;
        _connection.AndroidMirrorStartRequested -= OnAndroidMirrorStartRequested;
        UnregisterMirrorHandler();
        _activeSession?.Dispose();
    }
}
