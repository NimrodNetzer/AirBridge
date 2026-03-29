using AirBridge.App.Services;
using AirBridge.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AirBridge.App.ViewModels;

/// <summary>
/// Wraps a <see cref="DeviceInfo"/> with runtime UI state (e.g. reconnecting indicator).
/// </summary>
public sealed partial class DeviceItemViewModel : ObservableObject
{
    /// <summary>The underlying device model.</summary>
    public DeviceInfo Device { get; }

    private bool _isReconnecting;
    /// <summary>True while an automatic reconnect attempt is in progress for this device.</summary>
    public bool IsReconnecting
    {
        get => _isReconnecting;
        set => SetProperty(ref _isReconnecting, value);
    }

    /// <summary>Convenience passthrough: device name.</summary>
    public string DeviceName  => Device.DeviceName;
    /// <summary>Convenience passthrough: IP address.</summary>
    public string IpAddress   => Device.IpAddress;
    /// <summary>Convenience passthrough: port.</summary>
    public int    Port        => Device.Port;
    /// <summary>Convenience passthrough: device type.</summary>
    public DeviceType DeviceType => Device.DeviceType;
    /// <summary>Convenience passthrough: pairing state.</summary>
    public bool   IsPaired    => Device.IsPaired;
    /// <summary>Convenience passthrough: device ID.</summary>
    public string DeviceId    => Device.DeviceId;

    /// <summary>Creates a new wrapper around <paramref name="device"/>.</summary>
    public DeviceItemViewModel(DeviceInfo device) => Device = device;
}

/// <summary>
/// ViewModel for the Devices page. Exposes discovered devices and controls
/// for starting/stopping mDNS scanning and initiating a connection.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly DeviceConnectionService _connection;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    /// <summary>Live list of devices found on the LAN, wrapped with UI state.</summary>
    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    [ObservableProperty]
    private bool _isScanning = true;

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _statusMessage = "Scanning for devices\u2026";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>Raised when the user requests a connection to a device.</summary>
    public event EventHandler<DeviceInfo>? ConnectRequested;

    public DevicesViewModel(DeviceConnectionService connection)
    {
        _connection = connection;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Mirror raw DiscoveredDevices into Devices (wrapped)
        _connection.DiscoveredDevices.CollectionChanged += OnDiscoveredDevicesChanged;
        foreach (var d in _connection.DiscoveredDevices)
            Devices.Add(new DeviceItemViewModel(d));

        _connection.DeviceConnected    += OnDeviceConnected;
        _connection.DeviceDisconnected += OnDeviceDisconnected;
        _connection.DeviceReconnecting += OnDeviceReconnecting;
    }

    private void OnDiscoveredDevicesChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
                && e.NewItems is not null)
            {
                foreach (DeviceInfo d in e.NewItems)
                    Devices.Add(new DeviceItemViewModel(d));
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                     && e.OldItems is not null)
            {
                foreach (DeviceInfo d in e.OldItems)
                {
                    var vm = Devices.FirstOrDefault(x => x.DeviceId == d.DeviceId);
                    if (vm is not null) Devices.Remove(vm);
                }
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                Devices.Clear();
            }
        });
    }

    private void OnDeviceConnected(object? sender, string deviceId)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            var vm = Devices.FirstOrDefault(x => x.DeviceId == deviceId);
            if (vm is not null) vm.IsReconnecting = false;
            HasError = false;
        });
    }

    private void OnDeviceDisconnected(object? sender, string deviceId)
    {
        // Disconnection itself is not an error — reconnect may follow.
        // If no reconnect arrives, it will just stay disconnected.
    }

    private void OnDeviceReconnecting(object? sender, string deviceId)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            var vm = Devices.FirstOrDefault(x => x.DeviceId == deviceId);
            if (vm is not null) vm.IsReconnecting = true;
        });
    }

    /// <summary>Dismisses the current error banner.</summary>
    [RelayCommand]
    private void DismissError() => HasError = false;

    /// <summary>Starts or stops mDNS device scanning.</summary>
    [RelayCommand]
    private async Task ToggleScanAsync()
    {
        if (IsScanning)
        {
            await _connection.StopDiscoveryAsync();
            IsScanning     = false;
            StatusMessage  = "Scanning stopped";
        }
        else
        {
            IsScanning    = true;
            StatusMessage = "Scanning for devices\u2026";
            try
            {
                await _connection.StartDiscoveryAsync();
                HasError = false;
            }
            catch (Exception ex)
            {
                IsScanning    = false;
                StatusMessage = "Scan failed";
                ErrorMessage  = $"Failed to start scanning: {ex.Message}";
                HasError      = true;
            }
        }
    }

    /// <summary>Emits <see cref="ConnectRequested"/> for the given device.</summary>
    [RelayCommand]
    private void Connect(DeviceInfo? device)
    {
        if (device is null) return;
        ConnectRequested?.Invoke(this, device);
    }
}
