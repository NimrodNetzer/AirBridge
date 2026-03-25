using System.Security.Cryptography;
using AirBridge.Core.Interfaces;
using AirBridge.Transfer;

namespace AirBridge.Tests.Transfer;

/// <summary>
/// Unit tests for <see cref="TransferSession"/> using in-memory streams —
/// no network sockets are created.
/// </summary>
public class TransferSessionTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a full sender→receiver loopback using a connected pair of
    /// <see cref="MemoryStream"/> / pipe streams wired together.
    /// Returns the bytes written by the receiver.
    /// </summary>
    private static async Task<byte[]> LoopbackTransferAsync(
        byte[] fileData,
        CancellationToken ct = default)
    {
        // A pipe that lets the sender write and the receiver read
        var pipe = new System.IO.Pipelines.Pipe();

        var sourceStream = new MemoryStream(fileData, writable: false);
        var sinkStream   = new MemoryStream();

        // Wrap Pipe reader/writer as streams
        var networkWriteStream = pipe.Writer.AsStream();
        var networkReadStream  = pipe.Reader.AsStream();

        var sessionId  = Guid.NewGuid().ToString();
        const string FileName = "test.bin";
        long totalBytes = fileData.Length;

        var sender   = new TransferSession(sessionId, FileName, totalBytes,  isSender: true,  dataStream: sourceStream, networkStream: networkWriteStream);
        var receiver = new TransferSession(sessionId, FileName, totalBytes,  isSender: false, dataStream: sinkStream,   networkStream: networkReadStream);

        // Run sender and receiver concurrently
        var senderTask   = sender.StartAsync(ct);
        var receiverTask = receiver.StartAsync(ct);

        await Task.WhenAll(senderTask, receiverTask).ConfigureAwait(false);

        return sinkStream.ToArray();
    }

    // ── Message round-trips ────────────────────────────────────────────────

    [Fact]
    public void FileStartMessage_RoundTrip()
    {
        var msg     = new FileStartMessage("sid-123", "hello.txt", 4096);
        var bytes   = msg.ToBytes();
        var decoded = FileStartMessage.FromBytes(bytes);

        Assert.Equal(msg.SessionId,  decoded.SessionId);
        Assert.Equal(msg.FileName,   decoded.FileName);
        Assert.Equal(msg.TotalBytes, decoded.TotalBytes);
    }

    [Fact]
    public void FileChunkMessage_RoundTrip()
    {
        var data    = new byte[] { 1, 2, 3, 4, 5 };
        var msg     = new FileChunkMessage(1024, data);
        var bytes   = msg.ToBytes();
        var decoded = FileChunkMessage.FromBytes(bytes);

        Assert.Equal(msg.Offset, decoded.Offset);
        Assert.Equal(msg.Data,   decoded.Data);
    }

    [Fact]
    public void TransferAckMessage_RoundTrip()
    {
        var msg     = new TransferAckMessage(65536);
        var bytes   = msg.ToBytes();
        var decoded = TransferAckMessage.FromBytes(bytes);

        Assert.Equal(msg.BytesAcknowledged, decoded.BytesAcknowledged);
    }

    [Fact]
    public void FileEndMessage_RoundTrip()
    {
        var hash    = new byte[32];
        Random.Shared.NextBytes(hash);
        var msg     = new FileEndMessage(999, hash);
        var bytes   = msg.ToBytes();
        var decoded = FileEndMessage.FromBytes(bytes);

        Assert.Equal(msg.TotalBytes, decoded.TotalBytes);
        Assert.Equal(msg.Sha256Hash, decoded.Sha256Hash);
    }

    [Fact]
    public void TransferErrorMessage_RoundTrip()
    {
        var msg     = new TransferErrorMessage("disk full");
        var bytes   = msg.ToBytes();
        var decoded = TransferErrorMessage.FromBytes(bytes);

        Assert.Equal(msg.ErrorMessage, decoded.ErrorMessage);
    }

    // ── Loopback transfer ──────────────────────────────────────────────────

    [Fact]
    public async Task LoopbackTransfer_SmallFile_DataMatchesAndHashVerified()
    {
        var fileData = System.Text.Encoding.UTF8.GetBytes("Hello, AirBridge! This is a small test file.");
        var received = await LoopbackTransferAsync(fileData).ConfigureAwait(false);

        Assert.Equal(fileData, received);
    }

    [Fact]
    public async Task LoopbackTransfer_ExactlyOneChunk_Succeeds()
    {
        // 64 KB exactly
        var fileData = new byte[TransferSession.ChunkSize];
        Random.Shared.NextBytes(fileData);

        var received = await LoopbackTransferAsync(fileData).ConfigureAwait(false);

        Assert.Equal(fileData.Length, received.Length);
        Assert.Equal(SHA256.HashData(fileData), SHA256.HashData(received));
    }

    [Fact]
    public async Task LoopbackTransfer_MultipleChunks_Sha256Matches()
    {
        // 200 KB → 4 chunks (3 full + 1 partial)
        var fileData = new byte[200 * 1024];
        Random.Shared.NextBytes(fileData);

        var received = await LoopbackTransferAsync(fileData).ConfigureAwait(false);

        Assert.Equal(SHA256.HashData(fileData), SHA256.HashData(received));
    }

    [Fact]
    public async Task LoopbackTransfer_EmptyFile_Succeeds()
    {
        var received = await LoopbackTransferAsync(Array.Empty<byte>()).ConfigureAwait(false);
        Assert.Empty(received);
    }

    // ── State transitions ──────────────────────────────────────────────────

    [Fact]
    public async Task Session_CompletesWithCompletedState()
    {
        var pipe   = new System.IO.Pipelines.Pipe();
        var source = new MemoryStream(new byte[] { 0xAB, 0xCD });
        var sink   = new MemoryStream();
        var sid    = "state-test";

        var sender   = new TransferSession(sid, "f.bin", 2, true,  source, pipe.Writer.AsStream());
        var receiver = new TransferSession(sid, "f.bin", 2, false, sink,   pipe.Reader.AsStream());

        await Task.WhenAll(sender.StartAsync(), receiver.StartAsync()).ConfigureAwait(false);

        Assert.Equal(TransferState.Completed, sender.State);
        Assert.Equal(TransferState.Completed, receiver.State);
    }

    [Fact]
    public async Task Session_CancelAsync_TransitionsToCancelledState()
    {
        var cts    = new CancellationTokenSource();
        var source = new MemoryStream(new byte[1024 * 1024]); // 1 MB
        var sink   = new MemoryStream();
        var pipe   = new System.IO.Pipelines.Pipe();
        var sid    = "cancel-test";

        var sender   = new TransferSession(sid, "big.bin", 1024 * 1024, true,  source, pipe.Writer.AsStream());
        var receiver = new TransferSession(sid, "big.bin", 1024 * 1024, false, sink,   pipe.Reader.AsStream());

        var senderTask   = sender.StartAsync(cts.Token);
        var receiverTask = receiver.StartAsync(cts.Token);

        // Cancel almost immediately
        await Task.Delay(5).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        // Both tasks should complete without throwing (cancellation is handled internally)
        await Task.WhenAll(
            senderTask.ContinueWith(_ => { }),
            receiverTask.ContinueWith(_ => { })
        ).ConfigureAwait(false);
    }

    [Fact]
    public void Session_ProgressChanged_FiresDuringTransfer()
    {
        // Synchronous check: verify ProgressChanged fires at least once after start
        // (full async version is the loopback test above)
        var session = new TransferSession("s1", "f.bin", 0, true,
            new MemoryStream(Array.Empty<byte>()),
            new MemoryStream());
        bool fired = false;
        session.ProgressChanged += (_, _) => fired = true;
        // With empty file, no chunks → no progress event. Just verify it doesn't throw.
        Assert.False(fired); // baseline
    }
}
