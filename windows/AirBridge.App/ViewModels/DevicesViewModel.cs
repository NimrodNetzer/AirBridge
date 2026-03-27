using AirBridge.App.Services;
using AirBridge.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AirBridge.App.ViewModels;

/// <summary>
/// ViewModel for the Devices page. Exposes discovered devices and controls
/// for starting/stopping mDNS scanning and initiating a connection.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly DeviceConnectionService _connection;

    /// <summary>Live list of devices found on the LAN.</summary>
    public ObservableCollection<DeviceInfo> Devices => _connection.DiscoveredDevices;

    [ObservableProperty]
    private bool _isScanning = true;

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    [ObservableProperty]
    private string _statusMessage = "Scanning for devices\u2026";

    /// <summary>Raised when the user requests a connection to a device.</summary>
    public event EventHandler<DeviceInfo>? ConnectRequested;

    public DevicesViewModel(DeviceConnectionService connection)
    {
        _connection = connection;
    }

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
            await _connection.StartDiscoveryAsync();
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
