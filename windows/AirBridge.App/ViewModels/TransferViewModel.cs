using AirBridge.App.Services;
using AirBridge.Core.Models;
using AirBridge.Transfer;
using AirBridge.Transfer.Interfaces;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AirBridge.App.ViewModels;

/// <summary>Represents a single file transfer item shown in the transfer list.</summary>
public sealed class TransferItem : ObservableObject
{
    public string FileName { get; }
    public long TotalBytes { get; }

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
/// Write-only <see cref="Stream"/> that forwards raw bytes as
/// <c>FileChunk</c> protocol messages over an <see cref="IMessageChannel"/>.
/// </summary>
internal sealed class ChannelWriteStream : Stream
{
    private readonly IMessageChannel _channel;
    private readonly CancellationToken _ct;

    public ChannelWriteStream(IMessageChannel channel, CancellationToken ct)
    {
        _channel = channel;
        _ct = ct;
    }

    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override bool CanWrite => true;
    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var payload = buffer.AsSpan(offset, count).ToArray();
        _channel.SendAsync(new ProtocolMessage(MessageType.FileChunk, payload), _ct)
                .GetAwaiter().GetResult();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
        await _channel.SendAsync(
            new ProtocolMessage(MessageType.FileChunk, buffer.ToArray()), linked.Token)
            .ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// ViewModel for the File Transfer page. Manages the list of transfers and
/// drives sending files to the connected paired device.
/// </summary>
public sealed partial class TransferViewModel : ObservableObject
{
    private readonly IFileTransferService _transfer;
    private readonly DeviceConnectionService _connection;

    /// <summary>All current and recent transfer items.</summary>
    public ObservableCollection<TransferItem> Transfers { get; } = new();

    [ObservableProperty]
    private bool _canSend;

    [ObservableProperty]
    private string _connectedDeviceName = "No device connected";

    private IMessageChannel? _channel;

    public TransferViewModel(IFileTransferService transfer, DeviceConnectionService connection)
    {
        _transfer   = transfer;
        _connection = connection;

        // Reactively reflect session state changes from DeviceConnectionService.
        _connection.DeviceConnected    += OnDeviceConnected;
        _connection.DeviceDisconnected += OnDeviceDisconnected;

        // Reflect any already-connected device from before this ViewModel was created.
        var existingId = _connection.ConnectedDeviceIds.FirstOrDefault();
        if (existingId is not null)
        {
            _channel            = _connection.GetActiveSession(existingId);
            ConnectedDeviceName = existingId;
            CanSend             = _channel is not null;
        }
    }

    private void OnDeviceConnected(object? sender, string deviceId)
    {
        _channel = _connection.GetActiveSession(deviceId);
        ConnectedDeviceName = deviceId;
        CanSend = _channel is not null;
    }

    private void OnDeviceDisconnected(object? sender, string deviceId)
    {
        // Only clear if the disconnected device was our current target.
        if (_channel is not null && _channel.RemoteDeviceId == deviceId)
        {
            _channel            = null;
            ConnectedDeviceName = "No device connected";
            CanSend             = false;
        }
    }

    /// <summary>Sets the active connection target. Enables the Send button.</summary>
    public void SetTarget(DeviceInfo device, IMessageChannel channel)
    {
        _channel            = channel;
        ConnectedDeviceName = device.DeviceName;
        CanSend             = true;
    }

    /// <summary>Sends a file to the paired device over the active channel.</summary>
    [RelayCommand]
    private async Task SendFileAsync(string filePath)
    {
        if (_channel is null) return;

        var info = new FileInfo(filePath);
        var item = new TransferItem(info.Name, info.Length);
        Transfers.Add(item);

        try
        {
            var networkStream = new ChannelWriteStream(_channel, CancellationToken.None);
            await using (networkStream)
            {
                var service  = (FileTransferServiceImpl)_transfer;
                var progress = new Progress<long>(bytes =>
                {
                    item.Progress = info.Length > 0 ? bytes / (double)info.Length : 1.0;
                    item.Status   = $"{bytes / 1024} KB / {info.Length / 1024} KB";
                });

                await service.SendFileWithStreamAsync(filePath, networkStream, progress, CancellationToken.None);
            }

            item.Progress = 1.0;
            item.Status   = "Complete";
        }
        catch (Exception ex)
        {
            item.Status = $"Failed: {ex.Message}";
        }
    }
}
