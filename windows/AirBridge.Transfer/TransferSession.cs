using System.Security.Cryptography;
using AirBridge.Core.Interfaces;

namespace AirBridge.Transfer;

/// <summary>
/// Result type returned by <see cref="TransferSession"/> operations.
/// </summary>
public sealed record TransferResult(bool IsSuccess, string? ErrorMessage = null)
{
    /// <summary>Singleton success result.</summary>
    public static readonly TransferResult Success = new(true);

    /// <summary>Creates a failure result with the given message.</summary>
    public static TransferResult Failure(string message) => new(false, message);
}

/// <summary>
/// Implements <see cref="ITransferSession"/> for a single send or receive operation.
/// <para>
/// Chunked transfer (64 KB chunks) over any <see cref="Stream"/> pair.
/// SHA-256 is computed incrementally over all received bytes and verified when
/// the sender's <see cref="FileEndMessage"/> arrives.
/// Progress is reported via the <see cref="ProgressChanged"/> event and the
/// optional <see cref="IProgress{T}"/> supplied at construction.
/// Pause and cancel are implemented via <see cref="CancellationToken"/> cooperation:
/// pausing replaces the running token with a new linked source that the caller can
/// cancel to unblock the loop.
/// </para>
/// </summary>
public sealed class TransferSession : ITransferSession
{
    // ── Constants ──────────────────────────────────────────────────────────
    /// <summary>Default chunk size: 64 KB.</summary>
    public const int ChunkSize = 64 * 1024;

    // ── State ──────────────────────────────────────────────────────────────
    private TransferState _state = TransferState.Pending;
    private long _transferredBytes;
    private readonly object _stateLock = new();

    // Pause/cancel plumbing
    private CancellationTokenSource? _pauseCts;
    private readonly SemaphoreSlim   _pauseGate = new(1, 1);

    // Streams
    private readonly Stream _sendStream;    // source stream (sender) or ignored (receiver)
    private readonly Stream _receiveStream; // sink stream (receiver) or ignored (sender)
    private readonly IProgress<long>? _progress;

    // ── ITransferSession ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public string SessionId { get; }

    /// <inheritdoc/>
    public string FileName { get; }

    /// <inheritdoc/>
    public long TotalBytes { get; }

    /// <inheritdoc/>
    public long TransferredBytes => Volatile.Read(ref _transferredBytes);

    /// <inheritdoc/>
    public TransferState State
    {
        get { lock (_stateLock) return _state; }
        private set
        {
            lock (_stateLock) _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    /// <inheritdoc/>
    public bool IsSender { get; }

    /// <inheritdoc/>
    public event EventHandler<long>? ProgressChanged;

    /// <inheritdoc/>
    public event EventHandler<TransferState>? StateChanged;

    // ── Constructor ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="TransferSession"/>.
    /// </summary>
    /// <param name="sessionId">Unique identifier for this session.</param>
    /// <param name="fileName">Name of the file being transferred.</param>
    /// <param name="totalBytes">Total file size in bytes.</param>
    /// <param name="isSender">
    ///   <c>true</c> if this side is sending (reads from <paramref name="dataStream"/>);
    ///   <c>false</c> if this side is receiving (writes to <paramref name="dataStream"/>).
    /// </param>
    /// <param name="dataStream">
    ///   For the sender: the readable source stream (file or memory).
    ///   For the receiver: the writable destination stream.
    /// </param>
    /// <param name="networkStream">
    ///   The network (or pipe) stream used to exchange transfer messages with the peer.
    /// </param>
    /// <param name="progress">
    ///   Optional progress reporter; receives bytes-transferred updates after each chunk.
    /// </param>
    public TransferSession(
        string sessionId,
        string fileName,
        long totalBytes,
        bool isSender,
        Stream dataStream,
        Stream networkStream,
        IProgress<long>? progress = null)
    {
        SessionId      = sessionId   ?? throw new ArgumentNullException(nameof(sessionId));
        FileName       = fileName    ?? throw new ArgumentNullException(nameof(fileName));
        TotalBytes     = totalBytes;
        IsSender       = isSender;
        _sendStream    = isSender ? dataStream    : networkStream;
        _receiveStream = isSender ? networkStream : dataStream;
        _progress      = progress;
    }

    // ── ITransferSession lifecycle ─────────────────────────────────────────

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state != TransferState.Pending)
                throw new InvalidOperationException($"Cannot start a session in state {_state}.");
            _state = TransferState.Active;
        }
        AirBridge.Core.AppLog.Info($"[Transfer:{SessionId}] {FileName} Pending → Active (role={(IsSender ? "sender" : "receiver")}, totalBytes={TotalBytes})");
        StateChanged?.Invoke(this, TransferState.Active);

        _pauseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            if (IsSender)
                await RunSenderAsync(_pauseCts.Token).ConfigureAwait(false);
            else
                await RunReceiverAsync(_pauseCts.Token).ConfigureAwait(false);

            if (State == TransferState.Active)
            {
                AirBridge.Core.AppLog.Info($"[Transfer:{SessionId}] {FileName} Active → Completed");
                State = TransferState.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            if (State == TransferState.Paused)
                return; // caller will resume
            AirBridge.Core.AppLog.Info($"[Transfer:{SessionId}] {FileName} → Cancelled");
            State = TransferState.Cancelled;
        }
        catch (Exception ex)
        {
            AirBridge.Core.AppLog.Error($"[Transfer:{SessionId}] {FileName} → Failed", ex);
            State = TransferState.Failed;
            throw;
        }
    }

    /// <inheritdoc/>
    public Task PauseAsync()
    {
        lock (_stateLock)
        {
            if (_state != TransferState.Active) return Task.CompletedTask;
            _state = TransferState.Paused;
        }
        AirBridge.Core.AppLog.Info($"[Transfer:{SessionId}] {FileName} Active → Paused (at {TransferredBytes}/{TotalBytes} bytes)");
        StateChanged?.Invoke(this, TransferState.Paused);
        _pauseCts?.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state != TransferState.Paused)
                throw new InvalidOperationException($"Cannot resume a session in state {_state}.");
            _state = TransferState.Active;
        }
        StateChanged?.Invoke(this, TransferState.Active);

        _pauseCts?.Dispose();
        _pauseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            if (IsSender)
                await RunSenderAsync(_pauseCts.Token).ConfigureAwait(false);
            else
                await RunReceiverAsync(_pauseCts.Token).ConfigureAwait(false);

            if (State == TransferState.Active)
                State = TransferState.Completed;
        }
        catch (OperationCanceledException)
        {
            if (State == TransferState.Paused)
                return;
            State = TransferState.Cancelled;
        }
        catch (Exception)
        {
            State = TransferState.Failed;
            throw;
        }
    }

    /// <inheritdoc/>
    public Task CancelAsync()
    {
        State = TransferState.Cancelled;
        _pauseCts?.Cancel();
        return Task.CompletedTask;
    }

    // ── Sender loop ────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the source stream in 64 KB chunks, writes <see cref="FileChunkMessage"/>
    /// frames to the network stream, computes SHA-256, and finalises with a
    /// <see cref="FileEndMessage"/>.
    /// </summary>
    private async Task RunSenderAsync(CancellationToken ct)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkSize];
        long offset = TransferredBytes; // supports resume from non-zero position

        // Seek to resume offset if the stream supports it
        if (offset > 0 && _sendStream.CanSeek)
            _sendStream.Seek(offset, SeekOrigin.Begin);

        // Write FileStart header
        var startMsg = new FileStartMessage(SessionId, FileName, TotalBytes);
        await WriteMessageAsync(_receiveStream, startMsg.ToBytes(), ct).ConfigureAwait(false);

        int bytesRead;
        while ((bytesRead = await _sendStream.ReadAsync(buffer.AsMemory(0, ChunkSize), ct).ConfigureAwait(false)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            var chunkData = buffer[..bytesRead];
            sha256.AppendData(chunkData);

            var chunkMsg = new FileChunkMessage(offset, chunkData);
            await WriteMessageAsync(_receiveStream, chunkMsg.ToBytes(), ct).ConfigureAwait(false);

            offset += bytesRead;
            Interlocked.Add(ref _transferredBytes, bytesRead);
            var transferred = TransferredBytes;
            ProgressChanged?.Invoke(this, transferred);
            _progress?.Report(transferred);
        }

        var hash    = sha256.GetHashAndReset();
        var endMsg  = new FileEndMessage(offset, hash);
        await WriteMessageAsync(_receiveStream, endMsg.ToBytes(), ct).ConfigureAwait(false);
    }

    // ── Receiver loop ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads <see cref="FileChunkMessage"/> frames from the network stream,
    /// writes to the destination, accumulates SHA-256, and verifies against the
    /// digest in the final <see cref="FileEndMessage"/>.
    /// </summary>
    private async Task RunReceiverAsync(CancellationToken ct)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Read FileStart (may already have been consumed by the service layer;
        // if the stream position is past it, we skip this step).
        // In the loopback test model the session is created with the stream already positioned
        // after FileStart, so the caller should have read it. For simplicity we read it here.
        var startBytes = await ReadMessageAsync(_sendStream, ct).ConfigureAwait(false);
        // Consume but ignore (metadata already set in constructor from the service layer)
        _ = FileStartMessage.FromBytes(startBytes);

        long expectedOffset = TransferredBytes; // resume support

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var msgBytes = await ReadMessageAsync(_sendStream, ct).ConfigureAwait(false);
            var msgType  = (TransferMessageType)msgBytes[0];

            if (msgType == TransferMessageType.FileChunk)
            {
                var chunk = FileChunkMessage.FromBytes(msgBytes);
                if (chunk.Offset == expectedOffset)
                {
                    sha256.AppendData(chunk.Data);
                    await _receiveStream.WriteAsync(chunk.Data, ct).ConfigureAwait(false);
                    expectedOffset += chunk.Data.Length;
                    Interlocked.Add(ref _transferredBytes, chunk.Data.Length);
                    var transferred = TransferredBytes;
                    ProgressChanged?.Invoke(this, transferred);
                    _progress?.Report(transferred);
                }
                // Out-of-order or duplicate chunks are silently dropped (no resume in v1)
            }
            else if (msgType == TransferMessageType.FileEnd)
            {
                var endMsg       = FileEndMessage.FromBytes(msgBytes);
                var actualHash   = sha256.GetHashAndReset();
                if (!actualHash.SequenceEqual(endMsg.Sha256Hash))
                    throw new InvalidDataException("SHA-256 hash mismatch — file transfer corrupted.");
                break;
            }
            else if (msgType == TransferMessageType.TransferError)
            {
                var errMsg = TransferErrorMessage.FromBytes(msgBytes);
                throw new IOException($"Remote peer reported transfer error: {errMsg.ErrorMessage}");
            }
        }
    }

    // ── Wire helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes a length-prefixed message to <paramref name="stream"/>.
    /// Wire format: <c>[4-byte big-endian length][message bytes]</c>.
    /// </summary>
    internal static async Task WriteMessageAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lenBuf, message.Length);
        await stream.WriteAsync(lenBuf, ct).ConfigureAwait(false);
        await stream.WriteAsync(message, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one length-prefixed message from <paramref name="stream"/>.
    /// </summary>
    internal static async Task<byte[]> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false);
        int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        var buf = new byte[len];
        await ReadExactAsync(stream, buf, ct).ConfigureAwait(false);
        return buf;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct)
                                .ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("Stream ended before full message was read.");
            offset += n;
        }
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _pauseCts?.Cancel();
        _pauseCts?.Dispose();
        _pauseGate.Dispose();
    }
}
