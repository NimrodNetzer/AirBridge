using AirBridge.App.Services;
using AirBridge.App.ViewModels;
using AirBridge.Core.Models;
using Microsoft.UI.Xaml.Controls;

namespace AirBridge.App.Pages;

/// <summary>
/// ContentDialog that drives the TOFU pairing handshake.
/// Shows the 6-digit PIN in a large monospace font and updates status as the
/// handshake progresses.
/// </summary>
public sealed partial class PairingDialog : ContentDialog
{
    private readonly PairingViewModel _vm;

    /// <summary>Creates a new pairing dialog for the given remote device.</summary>
    public PairingDialog(DeviceInfo device)
    {
        InitializeComponent();

        _vm = App.Services.GetService(typeof(PairingViewModel)) as PairingViewModel
              ?? throw new InvalidOperationException("PairingViewModel not registered.");

        // Subscribe to PIN-ready before kicking off pairing so we don't miss the event
        var connectionSvc = App.Services.GetService(typeof(DeviceConnectionService)) as DeviceConnectionService
                            ?? throw new InvalidOperationException("DeviceConnectionService not registered.");

        connectionSvc.PairingPinReady += OnPinReady;

        _vm.PairingComplete += (_, _) =>
        {
            PinText.Text     = _vm.DisplayPin;
            StatusText.Text  = "Paired!";
            PairingProgress.IsIndeterminate = false;
            PairingProgress.Value = 100;
            // Auto-close after a short delay
            _ = Task.Delay(1200).ContinueWith(_ =>
                DispatcherQueue.TryEnqueue(Hide), TaskScheduler.Default);
        };

        // Kick off async pairing after the dialog is open
        Opened += async (_, _) =>
        {
            connectionSvc.PairingPinReady -= OnPinReady; // avoid double subscription on re-show
            connectionSvc.PairingPinReady += OnPinReady;
            await _vm.StartPairingAsync(device);

            if (!_vm.PairingSuccess)
            {
                StatusText.Text  = _vm.StatusMessage;
                PairingProgress.IsIndeterminate = false;
            }
        };
    }

    private void OnPinReady(object? sender, string pin)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Format as spaced digits: "1 2 3 4 5 6"
            PinText.Text    = string.Join(" ", pin.AsEnumerable());
            StatusText.Text = "Confirm this PIN on your Android device";
        });
    }
}
