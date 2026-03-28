using AirBridge.App.Services;
using AirBridge.Core.Models;
using AirBridge.Transfer;
using AirBridge.Transfer.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AirBridge.App.ViewModels;

/// <summary>Represents a single file transfer item shown in the transfer list.</summary>
public sealed class TransferItem : ObservableObject
{
    public string FileName   { get; }
    public long   TotalBytes { get; }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    private string _status = "Queued";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
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
        => RegisterDevice(deviceId);

    private void OnDeviceDisconnected(object? sender, string deviceId)
    {
        if (_connectedDeviceId != deviceId) return;
        _connectedDeviceId  = null;
        _transfer.SetChannel(null);
        ConnectedDeviceName = "No device connected";
        CanSend             = false;
    }

    private void RegisterDevice(string deviceId)
    {
        _connectedDeviceId  = deviceId;
        var channel         = _connection.GetActiveSession(deviceId);
        _transfer.SetChannel(channel);
        ConnectedDeviceName = deviceId;
        CanSend             = channel is not null;

        // Register receive handler so inbound transfers are written to Downloads/AirBridge.
        _connection.RemoveMessageHandlers(deviceId);
        _connection.AddMessageHandler(deviceId, _transfer.CreateReceiveHandler());
    }

    [RelayCommand]
    private async Task SendFileAsync(string filePath)
    {
        if (!CanSend || string.IsNullOrEmpty(filePath)) return;

        var info = new FileInfo(filePath);
        var item = new TransferItem(info.Name, info.Length);
        Transfers.Add(item);

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
            {
                item.Progress = info.Length > 0 ? bytes / (double)info.Length : 1.0;
                item.Status   = $"{bytes / 1024} KB / {info.Length / 1024} KB";
            });
            session.StateChanged += (_, state) => _dispatcher?.TryEnqueue(() =>
            {
                item.Status = state switch
                {
                    AirBridge.Core.Interfaces.TransferState.Completed => "Complete ✓",
                    AirBridge.Core.Interfaces.TransferState.Failed    => "Failed",
                    AirBridge.Core.Interfaces.TransferState.Cancelled => "Cancelled",
                    _ => item.Status,
                };
                if (state == AirBridge.Core.Interfaces.TransferState.Completed)
                    item.Progress = 1.0;
            });
        }
        catch (Exception ex)
        {
            item.Status = $"Failed: {ex.Message}";
        }
    }
}
