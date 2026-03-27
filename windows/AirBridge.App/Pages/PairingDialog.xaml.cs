using AirBridge.App.Services;
using AirBridge.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirBridge.App.Pages;

/// <summary>
/// ContentDialog that drives the incoming TOFU pairing handshake.
/// Waits for the Android device to initiate a connection, shows the
/// 6-digit PIN, and lets the user accept.
/// </summary>
public sealed partial class PairingDialog : ContentDialog
{
    private readonly DeviceConnectionService _connectionSvc;
    private bool _accepted;

    /// <summary>Creates a new pairing dialog. Device may be null when Android initiates.</summary>
    public PairingDialog(DeviceInfo? device)
    {
        InitializeComponent();

        _connectionSvc = App.Services.GetService(typeof(DeviceConnectionService)) as DeviceConnectionService
                         ?? throw new InvalidOperationException("DeviceConnectionService not registered.");

        StatusText.Text = "Open AirBridge on your Android device and tap Connect\u2026";
        PinText.Text    = "------";
        PairingProgress.IsIndeterminate = true;

        // When the inbound pairing request arrives, show the PIN
        _connectionSvc.IncomingPairingRequest += OnIncomingPairingRequest;

        // If Android already connected before the dialog opened, show the PIN immediately
        if (_connectionSvc.PendingPairingPin is { } existingPin)
            OnIncomingPairingRequest(this, (existingPin, string.Empty));

        PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; // prevent auto-close while we work
            IsPrimaryButtonEnabled = false;
            await AcceptAsync();
        };

        Closed += (_, _) =>
        {
            _connectionSvc.IncomingPairingRequest -= OnIncomingPairingRequest;
        };
    }

    private void OnIncomingPairingRequest(object? sender, (string Pin, string DeviceId) info)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PinText.Text    = string.Join(" ", info.Pin.AsEnumerable());
            StatusText.Text = "Confirm this PIN on your Android device, then click Accept";
            IsPrimaryButtonEnabled = true;
            PairingProgress.IsIndeterminate = false;
        });
    }

    private async Task AcceptAsync()
    {
        StatusText.Text = "Accepting\u2026";
        PairingProgress.IsIndeterminate = true;

        var success = await _connectionSvc.AcceptIncomingPairingAsync().ConfigureAwait(false);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (success)
            {
                PinText.Text    = "\u2713";
                StatusText.Text = "Paired successfully!";
            }
            else
            {
                StatusText.Text = "Pairing failed. Please try again.";
            }
            PairingProgress.IsIndeterminate = false;

            // Auto-close after short delay
            _ = Task.Delay(1200).ContinueWith(_ =>
                DispatcherQueue.TryEnqueue(Hide), TaskScheduler.Default);
        });
    }
}
