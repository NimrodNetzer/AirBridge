using System.Security.Cryptography;
using AirBridge.Tests.Helpers;
using AirBridge.Transfer;
using AirBridge.Core.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Tests.Transfer;

/// <summary>
/// End-to-end file transfer tests using <see cref="LoopbackChannelPair"/> — no network
/// sockets, no Android device required.  Each test runs in &lt;1 second.
///
/// This covers the exact production path:
///   Android (sender) ──[START / CHUNK / END]──▶ Windows (FileTransferServiceImpl receiver)
/// </summary>
public class FileTransferIntegrationTests : IDisposable
{
    private readonly string _receiveDir =
        Path.Combine(Path.GetTempPath(), $"AirBridge_Tests_{Guid.NewGuid():N}");

    public FileTransferIntegrationTests() => Directory.CreateDirectory(_receiveDir);

    public void Dispose()
    {
        try { Directory.Delete(_receiveDir, recursive: true); } catch { }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private FileTransferServiceImpl MakeReceiver(AirBridge.Transport.Interfaces.IMessageChannel channel)
    {
        var svc = new FileTransferServiceImpl(_receiveDir);
        svc.SetChannel(channel);
        return svc;
    }

    /// <summary>Simulates Android sending a file over <paramref name="sender"/>.</summary>
    private static async Task SendFileAsync(
        AirBridge.Transport.Interfaces.IMessageChannel sender,
        string fileName,
        byte[] data,
        int chunkSize = 64 * 1024)
    {
        var sessionId = Guid.NewGuid().ToString();

        await sender.SendAsync(new ProtocolMessage(MessageType.FileTransferStart,
            new FileStartMessage(sessionId, fileName, data.Length).ToBytes()));

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long offset = 0;
        while (offset < data.Length)
        {
            int take  = (int)Math.Min(chunkSize, data.Length - offset);
            var chunk = data[(int)offset..(int)(offset + take)];
            sha.AppendData(chunk);
            await sender.SendAsync(new ProtocolMessage(MessageType.FileChunk,
                new FileChunkMessage(offset, chunk).ToBytes()));
            offset += take;
        }

        await sender.SendAsync(new ProtocolMessage(MessageType.FileTransferEnd,
            new FileEndMessage(data.Length, sha.GetCurrentHash()).ToBytes()));

        await sender.DisposeAsync(); // signals no more messages
    }

    /// <summary>Pumps all messages from <paramref name="channel"/> into <paramref name="handler"/>.</summary>
    private static async Task DrainAsync(
        AirBridge.Transport.Interfaces.IMessageChannel channel,
        Func<ProtocolMessage, Task> handler)
    {
        while (true)
        {
            var msg = await channel.ReceiveAsync();
            if (msg is null) break;
            await handler(msg);
        }
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SmallFile_ReceivedCorrectly()
    {
        var data = RandomBytes(1_024);
        const string name = "small.bin";

        var pair     = LoopbackChannelPair.Create();
        var receiver = MakeReceiver(pair.B);
        var handler  = receiver.CreateReceiveHandler();

        await Task.WhenAll(
            SendFileAsync(pair.A, name, data),
            DrainAsync(pair.B, handler));

        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_receiveDir, name)));
    }

    [Fact]
    public async Task LargeFile_MultiChunk_ReceivedCorrectly()
    {
        // 5 MB — exercises multi-chunk path (64 KB chunks → ~80 messages)
        var data = RandomBytes(5 * 1024 * 1024);
        const string name = "large.bin";

        var pair     = LoopbackChannelPair.Create();
        var receiver = MakeReceiver(pair.B);
        var handler  = receiver.CreateReceiveHandler();

        await Task.WhenAll(
            SendFileAsync(pair.A, name, data),
            DrainAsync(pair.B, handler));

        var received = await File.ReadAllBytesAsync(Path.Combine(_receiveDir, name));
        Assert.Equal(data.Length, received.Length);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(received));
    }

    [Fact]
    public async Task EmptyFile_ReceivedCorrectly()
    {
        const string name = "empty.bin";
        var pair     = LoopbackChannelPair.Create();
        var receiver = MakeReceiver(pair.B);
        var handler  = receiver.CreateReceiveHandler();

        await Task.WhenAll(
            SendFileAsync(pair.A, name, Array.Empty<byte>()),
            DrainAsync(pair.B, handler));

        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(_receiveDir, name)));
    }

    [Fact]
    public async Task CorruptedHash_SessionMarkedFailed()
    {
        var data      = RandomBytes(512);
        const string name = "corrupt.bin";
        var sessionId = Guid.NewGuid().ToString();

        var pair     = LoopbackChannelPair.Create();
        var receiver = MakeReceiver(pair.B);
        var handler  = receiver.CreateReceiveHandler();

        // Send with wrong hash (all zeros)
        await pair.A.SendAsync(new ProtocolMessage(MessageType.FileTransferStart,
            new FileStartMessage(sessionId, name, data.Length).ToBytes()));
        await pair.A.SendAsync(new ProtocolMessage(MessageType.FileChunk,
            new FileChunkMessage(0, data).ToBytes()));
        await pair.A.SendAsync(new ProtocolMessage(MessageType.FileTransferEnd,
            new FileEndMessage(data.Length, new byte[32]).ToBytes())); // wrong hash
        await pair.A.DisposeAsync();

        await DrainAsync(pair.B, handler);

        var sessions = receiver.GetActiveSessions().ToList();
        Assert.Single(sessions);
        Assert.Equal(TransferState.Failed, sessions[0].State);
    }

    [Fact]
    public async Task TwoConsecutiveFiles_BothReceivedCorrectly()
    {
        var file1 = RandomBytes(256);
        var file2 = RandomBytes(512);

        // Use a fresh pair for each file (SimulateAndroidSend disposes the sender channel)
        async Task SendAndReceive(byte[] d, string n)
        {
            var pair     = LoopbackChannelPair.Create();
            var receiver = MakeReceiver(pair.B);
            var handler  = receiver.CreateReceiveHandler();
            await Task.WhenAll(SendFileAsync(pair.A, n, d), DrainAsync(pair.B, handler));
        }

        await SendAndReceive(file1, "file1.bin");
        await SendAndReceive(file2, "file2.bin");

        Assert.Equal(file1, await File.ReadAllBytesAsync(Path.Combine(_receiveDir, "file1.bin")));
        Assert.Equal(file2, await File.ReadAllBytesAsync(Path.Combine(_receiveDir, "file2.bin")));
    }

    // ── Utilities ──────────────────────────────────────────────────────────

    private static byte[] RandomBytes(int length)
    {
        var buf = new byte[length];
        Random.Shared.NextBytes(buf);
        return buf;
    }
}
