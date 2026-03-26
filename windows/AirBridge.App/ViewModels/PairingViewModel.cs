using AirBridge.App.Services;
using AirBridge.Core.Models;
using AirBridge.Transport.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AirBridge.App.ViewModels;

/// <summary>
/// ViewModel for the PIN confirmation pairing dialog.
/// Connects to the remote device, drives the pairing handshake, and
/// surfaces the 6-digit PIN for the user to confirm on both devices.
/// </summary>
public sealed partial class PairingViewModel : ObservableObject
{
    private readonly DeviceConnectionService _connection;
    private IMessageChannel? _channel;

    [ObservableProperty]
    private string _displayPin = "------";

    [ObservableProperty]
    private bool _isPairing;

    [ObservableProperty]
    private bool _pairingSuccess;

    [ObservableProperty]
    private string _statusMessage = "Waiting\u2026";

    /// <summary>Raised when pairing completes successfully.</summary>
    public event EventHandler? PairingComplete;

    public PairingViewModel(DeviceConnectionService connection)
    {
        _connection = connection;
        _connection.PairingPinReady += (_, pin) =>
        {
            DisplayPin    = pin;
            StatusMessage = "Confirm this PIN on both devices";
        };
    }

    /// <summary>
    /// Opens a connection to <paramref name="device"/> and runs the pairing handshake.
    /// </summary>
    public async Task StartPairingAsync(DeviceInfo device)
    {
        IsPairing     = true;
        StatusMessage = "Connecting\u2026";
        try
        {
            _channel      = await _connection.ConnectToDeviceAsync(device, CancellationToken.None);
            StatusMessage = "Exchanging keys\u2026";
            var success   = await _connection.PairAsync(device, _channel, CancellationToken.None);
            PairingSuccess = success;
            StatusMessage  = success ? "Paired successfully!" : "Pairing failed";
            if (success) PairingComplete?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsPairing = false;
        }
    }

    /// <summary>Cancels the in-progress pairing and closes the channel.</summary>
    [RelayCommand]
    private async Task CancelAsync()
    {
        if (_channel is not null)
            await _channel.DisposeAsync();
        StatusMessage = "Pairing cancelled";
    }
}
