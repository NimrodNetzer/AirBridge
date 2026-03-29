using AirBridge.App.Services;
using AirBridge.Core.Models;
using AirBridge.Transfer;
using AirBridge.Transfer.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace AirBridge.App.ViewModels;

/// <summary>Represents a single file transfer item shown in the transfer list.</summary>
public sealed class TransferItem : ObservableObject
{
    public string FileName   { get; }
    public long   TotalBytes { get; }

    private double _progress;
    /// <summary>Transfer progress in the range [0, 1].</summary>
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    private string _status = "Queued";
    /// <summary>Human-readable status label (e.g. "Queued", "Complete ✓", "Failed").</summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private string _speedText = string.Empty;
    /// <summary>Formatted transfer speed, e.g. "1.2 MB/s".</summary>
    public string SpeedText
    {
        get => _speedText;
        set => SetProperty(ref _speedText, value);
    }

    private string _etaText = string.Empty;
    /// <summary>Formatted estimated time remaining, e.g. "~3s" or "~1m 20s".</summary>
    public string EtaText
    {
        get => _etaText;
        set => SetProperty(ref _etaText, value);
    }

    private bool _isActive;
    /// <summary>True while the transfer is actively sending/receiving bytes.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    // ── Speed / ETA tracking ─────────────────────────────────────────────
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _lastBytes;

    /// <summary>
    /// Updates <see cref="Progress"/>, <see cref="SpeedText"/>, and <see cref="EtaText"/>
    /// from a raw byte-transferred count.
    /// </summary>
    public void UpdateProgress(long bytesTransferred)
    {
        Progress  = TotalBytes > 0 ? bytesTransferred / (double)TotalBytes : 1.0;
        IsActive  = true;

        var elapsedSec = _stopwatch.Elapsed.TotalSeconds;
        if (elapsedSec > 0.1)
        {
            var deltaBytes = bytesTransferred - _lastBytes;
            var speedBps   = deltaBytes / elapsedSec; // bytes per second since last update

            SpeedText = FormatSpeed(speedBps);

            var remaining = TotalBytes - bytesTransferred;
            if (speedBps > 0)
                EtaText = FormatEta(remaining / speedBps);
            else
                EtaText = string.Empty;

            _lastBytes = bytesTransferred;
            _stopwatch.Restart();
        }

        Status = $"{FormatBytes(bytesTransferred)} / {FormatBytes(TotalBytes)}";
    }

    private static string FormatSpeed(double bytesPerSec)
        => bytesPerSec switch
        {
            >= 1024 * 1024 * 1024 => $"{bytesPerSec / (1024.0 * 1024 * 1024):F1} GB/s",
            >= 1024 * 1024        => $"{bytesPerSec / (1024.0 * 1024):F1} MB/s",
            >= 1024               => $"{bytesPerSec / 1024.0:F0} KB/s",
            _                     => $"{bytesPerSec:F0} B/s"
        };

    private static string FormatBytes(long bytes)
        => bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
            >= 1024 * 1024         => $"{bytes / (1024.0 * 1024):F1} MB",
            >= 1024                => $"{bytes / 1024.0:F0} KB",
            _                      => $"{bytes} B"
        };

    private static string FormatEta(double seconds)
    {
        if (seconds < 60) return $"~{(int)seconds}s";
        var m = (int)(seconds / 60);
        var s = (int)(seconds % 60);
        return $"~{m}m {s}s";
    }

    public TransferItem(string fileName, long totalBytes)
    {
        FileName   = fileName;
        TotalBytes = totalBytes;
    }
}

/// <summary>
/// ViewModel for the File Transfer page.
/// Bridges the file-picker to <see cref="FileTransferServiceImpl"/> and tracks active transfers.
/// </summary>
public sealed partial class TransferViewModel : ObservableObject
{
    private readonly FileTransferServiceImpl _transfer;
    private readonly DeviceConnectionService _connection;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public ObservableCollection<TransferItem> Transfers { get; } = new();

    [ObservableProperty] private bool   _canSend;
    [ObservableProperty] private string _connectedDeviceName = "No device connected";

    [ObservableProperty] private bool   _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;

    /// <summary>
    /// Path of the last file attempted, stored so the Retry command can re-trigger the send.
    /// </summary>
    private string? _lastFilePath;

    private string? _connectedDeviceId;

    public TransferViewModel(IFileTransferService transfer, DeviceConnectionService connection)
    {
        _transfer   = (FileTransferServiceImpl)transfer;
        _connection = connection;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _connection.DeviceConnected    += OnDeviceConnected;
        _connection.DeviceDisconnected += OnDeviceDisconnected;

        // Register receive handler for inbound transfers.
        // We register once; it stays active for the lifetime of this ViewModel.
        // (If the device ID changes the handler is re-registered in OnDeviceConnected.)

        // Pick up any already-connected device.
        var existingId = _connection.ConnectedDeviceIds.FirstOrDefault();
        if (existingId is not null) RegisterDevice(existingId);
    }

    private void OnDeviceConnected(object? sender, string deviceId)
    {
        RegisterDevice(deviceId);
        _dispatcher?.TryEnqueue(() => HasError = false);
    }

    private void OnDeviceDisconnected(object? sender, string deviceId)
    {
        if (_connectedDeviceId != deviceId) return;
        _connectedDeviceId  = null;
        _transfer.SetChannel(null);
        _dispatcher?.TryEnqueue(() =>
        {
            ConnectedDeviceName = "No device connected";
            CanSend             = false;
            // Show a non-fatal banner — the connection manager will auto-reconnect.
            ErrorMessage        = $"Connection to {deviceId} lost. Reconnecting\u2026";
            HasError            = true;
        });
    }

    private void RegisterDevice(string deviceId)
    {
        _connectedDeviceId  = deviceId;
        var channel         = _connection.GetActiveSession(deviceId);
        _transfer.SetChannel(channel);
        _dispatcher?.TryEnqueue(() =>
        {
            ConnectedDeviceName = deviceId;
            CanSend             = channel is not null;
        });

        // Register receive handler so inbound transfers are written to Downloads/AirBridge.
        _connection.RemoveMessageHandlers(deviceId);
        _connection.AddMessageHandler(deviceId, _transfer.CreateReceiveHandler());
    }

    /// <summary>Dismisses the current error banner.</summary>
    [RelayCommand]
    private void DismissError() => HasError = false;

    /// <summary>Retries the last failed send operation.</summary>
    [RelayCommand]
    private async Task RetrySendAsync()
    {
        HasError = false;
        if (_lastFilePath is not null)
            await SendFileAsync(_lastFilePath).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SendFileAsync(string filePath)
    {
        if (!CanSend || string.IsNullOrEmpty(filePath)) return;

        _lastFilePath = filePath;

        var info = new FileInfo(filePath);
        var item = new TransferItem(info.Name, info.Length);
        _dispatcher?.TryEnqueue(() => Transfers.Add(item));

        try
        {
            var fakeDevice = new DeviceInfo(
                DeviceId:   _connectedDeviceId ?? string.Empty,
                DeviceName: ConnectedDeviceName,
                DeviceType: DeviceType.AndroidPhone,
                IpAddress:  string.Empty,
                Port:       0,
                IsPaired:   true);

            var session = await _transfer.SendFileAsync(filePath, fakeDevice).ConfigureAwait(false);

            session.ProgressChanged += (_, bytes) => _dispatcher?.TryEnqueue(() =>
                item.UpdateProgress(bytes));

            session.StateChanged += (_, state) => _dispatcher?.TryEnqueue(() =>
            {
                switch (state)
                {
                    case AirBridge.Core.Interfaces.TransferState.Completed:
                        item.Status   = "Complete \u2713";
                        item.Progress = 1.0;
                        item.IsActive = false;
                        item.SpeedText = string.Empty;
                        item.EtaText   = string.Empty;
                        HasError = false;
                        break;
                    case AirBridge.Core.Interfaces.TransferState.Failed:
                        item.Status   = "Failed";
                        item.IsActive = false;
                        ErrorMessage  = $"Transfer of \u201c{item.FileName}\u201d failed.";
                        HasError      = true;
                        break;
                    case AirBridge.Core.Interfaces.TransferState.Cancelled:
                        item.Status   = "Cancelled";
                        item.IsActive = false;
                        break;
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher?.TryEnqueue(() =>
            {
                item.Status  = "Failed";
                item.IsActive = false;
                ErrorMessage = $"Could not send \u201c{info.Name}\u201d: {ex.Message}";
                HasError     = true;
            });
        }
    }
}
