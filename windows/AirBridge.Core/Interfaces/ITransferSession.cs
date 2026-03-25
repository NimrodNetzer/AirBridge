namespace AirBridge.Core.Interfaces;

/// <summary>State of a file transfer session.</summary>
public enum TransferState { Pending, Active, Paused, Completed, Failed, Cancelled }

/// <summary>
/// Represents a single file transfer session (send or receive).
/// Progress is reported via <see cref="ProgressChanged"/>.
/// </summary>
public interface ITransferSession : IDisposable
{
    string SessionId { get; }
    string FileName { get; }
    long TotalBytes { get; }
    long TransferredBytes { get; }
    TransferState State { get; }
    bool IsSender { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task PauseAsync();
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task CancelAsync();

    event EventHandler<long> ProgressChanged;     // arg: bytes transferred so far
    event EventHandler<TransferState> StateChanged;
}
