using AirBridge.Core;
using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Transfer.Interfaces;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AirBridge.Transfer;

/// <summary>
/// Production implementation of <see cref="IFileTransferService"/>.
///
/// Sends files over the active <see cref="IMessageChannel"/> set via <see cref="SetChannel"/>.
/// Call <see cref="CreateReceiveHandler"/> to get a delegate for inbound transfers and register
/// it with <c>DeviceConnectionService.AddMessageHandler</c>.
///
/// Wire protocol (both platforms must agree):
/// <list type="bullet">
///   <item>FILE_TRANSFER_START (0x10) — <see cref="FileStartMessage"/></item>
///   <item>FILE_CHUNK          (0x11) — <see cref="FileChunkMessage"/></item>
///   <item>FILE_TRANSFER_END   (0x13) — <see cref="FileEndMessage"/></item>
/// </list>
/// </summary>
public sealed class FileTransferServiceImpl : IFileTransferService, IDisposable
{
    private const int ChunkSize = 64 * 1024; // 64 KB

    private readonly ConcurrentDictionary<string, ITransferSession> _sessions = new();

    private IMessageChannel? _channel;

    // Receive state machine (one in-progress receive at a time)
    private TransferProgressSession? _rxSession;
    private FileStream?              _rxStream;
    private IncrementalHash?         _rxHash;

    /// <summary>
    /// Optional override for the directory where received files are saved.
    /// When null, defaults to <c>~/Downloads/AirBridge</c>.
    /// </summary>
    private readonly string? _receiveDirOverride;

    /// <summary>Creates a service that saves received files to <c>~/Downloads/AirBridge</c>.</summary>
    public FileTransferServiceImpl() { }

    /// <summary>Creates a service that saves received files to <paramref name="receiveDir"/>.</summary>
    public FileTransferServiceImpl(string receiveDir) => _receiveDirOverride = receiveDir;

    private string ReceiveDir
    {
        get
        {
            var dir = _receiveDirOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "AirBridge");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ── Channel wiring ─────────────────────────────────────────────────────

    /// <summary>
    /// Sets the active channel used for outbound transfers.
    /// Pass <see langword="null"/> to clear when a device disconnects.
    /// </summary>
    public void SetChannel(IMessageChannel? channel) => _channel = channel;

    // ── IFileTransferService ───────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<ITransferSession> SendFileAsync(
        string filePath,
        DeviceInfo destination,
        CancellationToken cancellationToken = default)
    {
        var channel = _channel
            ?? throw new InvalidOperationException(
                $"No active session for '{destination.DeviceId}'. Connect before sending.");

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found.", filePath);

        var info      = new FileInfo(filePath);
        var sessionId = Guid.NewGuid().ToString("N");
        var session   = new TransferProgressSession(sessionId, info.Name, info.Length, isSender: true);
        _sessions[sessionId] = session;

        _ = Task.Run(async () =>
        {
            try
            {
                // Transition to Active inside the task so the caller can subscribe to
                // StateChanged/ProgressChanged before the first event fires.
                session.UpdateState(TransferState.Active);

                // 1. FILE_TRANSFER_START
                AppLog.Info($"sendFile START: {info.Name} ({info.Length} bytes) → {destination.DeviceId}");
                var startBytes = new FileStartMessage(sessionId, info.Name, info.Length).ToBytes();
                await channel.SendAsync(
                    new ProtocolMessage(MessageType.FileTransferStart, startBytes),
                    cancellationToken).ConfigureAwait(false);

                // 2. FILE_CHUNK stream with SHA-256
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long offset = 0;
                int chunkIndex = 0;
                var buf = new byte[ChunkSize];
                await using var src = info.OpenRead();
                int read;
                while ((read = await src.ReadAsync(buf, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    var chunk = buf[..read];
                    sha.AppendData(chunk);
                    var chunkBytes = new FileChunkMessage(offset, chunk).ToBytes();
                    AppLog.Info($"sendFile CHUNK #{chunkIndex}: offset={offset} size={read}");
                    await channel.SendAsync(
                        new ProtocolMessage(MessageType.FileChunk, chunkBytes),
                        cancellationToken).ConfigureAwait(false);
                    AppLog.Info($"sendFile CHUNK #{chunkIndex} sent OK");
                    offset += read;
                    chunkIndex++;
                    session.UpdateProgress(offset);
                }

                // 3. FILE_TRANSFER_END
                AppLog.Info($"sendFile END: {info.Name} — {chunkIndex} chunks, {offset} bytes");
                var endBytes = new FileEndMessage(offset, sha.GetCurrentHash()).ToBytes();
                await channel.SendAsync(
                    new ProtocolMessage(MessageType.FileTransferEnd, endBytes),
                    cancellationToken).ConfigureAwait(false);

                session.UpdateState(TransferState.Completed);
                AppLog.Info($"sendFile COMPLETE: {info.Name}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"sendFile FAILED: {info.Name} — {ex.GetType().Name}: {ex.Message}", ex);
                session.UpdateState(TransferState.Failed);
                try
                {
                    var errBytes = new TransferErrorMessage(ex.Message).ToBytes();
                    await channel.SendAsync(
                        new ProtocolMessage(MessageType.Error, errBytes),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* ignore send failure on error path */ }
            }
            finally { _sessions.TryRemove(sessionId, out _); }
        }, cancellationToken);

        return session;
    }

    /// <inheritdoc/>
    public Task<ITransferSession> ReceiveFileAsync(
        string sessionId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "Inbound transfers are handled automatically via CreateReceiveHandler().");

    /// <inheritdoc/>
    public IReadOnlyList<ITransferSession> GetActiveSessions()
        => _sessions.Values.ToList();

    // ── Inbound handler ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a message handler delegate for
    /// <c>DeviceConnectionService.AddMessageHandler</c>.
    /// Handles FILE_TRANSFER_START / FILE_CHUNK / FILE_TRANSFER_END and saves
    /// received files to <see cref="ReceiveDir"/>.
    /// </summary>
    public Func<ProtocolMessage, Task> CreateReceiveHandler() => async msg =>
    {
        switch (msg.Type)
        {
            case MessageType.FileTransferStart:
            {
                var start  = FileStartMessage.FromBytes(msg.Payload);
                var outPath = Path.Combine(ReceiveDir, start.FileName);
                _rxStream  = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                                            FileShare.None, 65536, useAsync: true);
                _rxHash    = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                _rxSession = new TransferProgressSession(
                    start.SessionId, start.FileName, start.TotalBytes, isSender: false);
                _sessions[start.SessionId] = _rxSession;
                _rxSession.UpdateState(TransferState.Active);
                break;
            }
            case MessageType.FileChunk:
            {
                if (_rxStream is null || _rxSession is null) break;
                var chunk = FileChunkMessage.FromBytes(msg.Payload);
                _rxHash!.AppendData(chunk.Data);
                await _rxStream.WriteAsync(chunk.Data).ConfigureAwait(false);
                _rxSession.UpdateProgress(chunk.Offset + chunk.Data.Length);
                break;
            }
            case MessageType.FileTransferEnd:
            {
                if (_rxStream is null || _rxSession is null) break;
                var end = FileEndMessage.FromBytes(msg.Payload);
                await _rxStream.FlushAsync().ConfigureAwait(false);
                _rxStream.Dispose();
                var ok = _rxHash!.GetCurrentHash().SequenceEqual(end.Sha256Hash);
                _rxSession.UpdateState(ok ? TransferState.Completed : TransferState.Failed);
                ResetRxState();
                break;
            }
            case MessageType.Error:
            {
                _rxStream?.Dispose();
                _rxSession?.UpdateState(TransferState.Failed);
                ResetRxState();
                break;
            }
        }
    };

    private void ResetRxState()
    {
        _rxStream  = null;
        _rxHash?.Dispose(); _rxHash = null;
        _rxSession = null;
    }

    public void Dispose()
    {
        _rxStream?.Dispose();
        _rxHash?.Dispose();
    }
}

// ── TransferProgressSession ────────────────────────────────────────────────

/// <summary>
/// Concrete <see cref="ITransferSession"/> implementation used for both send and receive.
/// Progress and state are updated by <see cref="FileTransferServiceImpl"/>.
/// </summary>
internal sealed class TransferProgressSession : ITransferSession
{
    public string        SessionId        { get; }
    public string        FileName         { get; }
    public long          TotalBytes       { get; }
    public bool          IsSender         { get; }
    public long          TransferredBytes { get; private set; }
    public TransferState State            { get; private set; } = TransferState.Pending;

    public event EventHandler<long>?          ProgressChanged;
    public event EventHandler<TransferState>? StateChanged;

    public TransferProgressSession(string sessionId, string fileName, long totalBytes, bool isSender)
    {
        SessionId  = sessionId;
        FileName   = fileName;
        TotalBytes = totalBytes;
        IsSender   = isSender;
    }

    public void UpdateProgress(long transferred)
    {
        TransferredBytes = transferred;
        ProgressChanged?.Invoke(this, transferred);
    }

    public void UpdateState(TransferState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public Task StartAsync(CancellationToken ct = default)  => Task.CompletedTask;
    public Task PauseAsync()                                => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CancelAsync()
    {
        UpdateState(TransferState.Cancelled);
        return Task.CompletedTask;
    }

    public void Dispose() { /* nothing to release */ }
}
