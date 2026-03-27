using System.Diagnostics;
using AirBridge.Transfer;

namespace AirBridge.Tests.Performance;

/// <summary>
/// Self-timing transfer throughput benchmark.
///
/// Sends a 100 MB in-memory payload through the <see cref="TransferSession"/> chunking
/// and SHA-256 pipeline over an in-process loopback pipe, then asserts that throughput
/// exceeds 50 MB/s — a conservative floor for localhost loopback on any modern machine.
///
/// The test does NOT use BenchmarkDotNet; it uses <see cref="Stopwatch"/> for timing.
/// This is intentional — BenchmarkDotNet is not in the project's test dependencies
/// (see AirBridge.Tests.csproj), and the goal is a regression gate, not a micro-benchmark.
/// </summary>
public class TransferThroughputBenchmark
{
    private const int  OneMB        = 1024 * 1024;
    private const int  FileSize     = 100 * OneMB;  // 100 MB
    private const double MinMBps    = 50.0;          // minimum acceptable throughput

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a sender→receiver loopback using an in-process <see cref="System.IO.Pipelines.Pipe"/>.
    /// Returns the elapsed <see cref="TimeSpan"/> for the transfer phase only.
    /// </summary>
    private static async Task<TimeSpan> RunLoopbackTransferAsync(byte[] fileData)
    {
        var pipe = new System.IO.Pipelines.Pipe();

        var sourceStream = new MemoryStream(fileData, writable: false);
        var sinkStream   = new MemoryStream(fileData.Length);

        var networkWriteStream = pipe.Writer.AsStream();
        var networkReadStream  = pipe.Reader.AsStream();

        var sessionId  = Guid.NewGuid().ToString();
        const string FileName = "benchmark.bin";
        long totalBytes = fileData.Length;

        var sender   = new TransferSession(sessionId, FileName, totalBytes, isSender: true,  dataStream: sourceStream, networkStream: networkWriteStream);
        var receiver = new TransferSession(sessionId, FileName, totalBytes, isSender: false, dataStream: sinkStream,   networkStream: networkReadStream);

        var sw = Stopwatch.StartNew();

        await Task.WhenAll(
            sender.StartAsync(),
            receiver.StartAsync()
        );

        sw.Stop();
        return sw.Elapsed;
    }

    // ── Benchmark tests ───────────────────────────────────────────────────────

    /// <summary>
    /// Sends 100 MB through the loopback transfer pipeline and asserts > 50 MB/s throughput.
    /// </summary>
    [Fact]
    public async Task TransferThroughput_100MB_Exceeds50MBps()
    {
        // Arrange — prepare 100 MB of random data
        var fileData = new byte[FileSize];
        Random.Shared.NextBytes(fileData);

        // Act — warm up (first run allocates JIT, buffers, etc.)
        await RunLoopbackTransferAsync(new byte[OneMB]);

        // Timed run
        var elapsed = await RunLoopbackTransferAsync(fileData);

        // Assert
        double seconds  = elapsed.TotalSeconds;
        double mbTransferred = (double)FileSize / OneMB;
        double mbps     = mbTransferred / seconds;

        // Report to test output
        Console.WriteLine($"[TransferThroughput] {mbTransferred:F0} MB in {seconds:F3}s → {mbps:F1} MB/s");

        Assert.True(mbps >= MinMBps,
            $"Transfer throughput {mbps:F1} MB/s is below the minimum {MinMBps} MB/s. " +
            $"Elapsed: {elapsed.TotalMilliseconds:F0} ms for {mbTransferred:F0} MB.");
    }

    /// <summary>
    /// Verifies that a 1 MB transfer completes within 1 second (basic sanity check).
    /// </summary>
    [Fact]
    public async Task TransferThroughput_1MB_CompletesWithin1Second()
    {
        var fileData = new byte[OneMB];
        Random.Shared.NextBytes(fileData);

        var elapsed = await RunLoopbackTransferAsync(fileData);

        Console.WriteLine($"[TransferThroughput] 1 MB in {elapsed.TotalMilliseconds:F0} ms");
        Assert.True(elapsed.TotalSeconds < 1.0,
            $"1 MB transfer took {elapsed.TotalMilliseconds:F0} ms — expected < 1000 ms.");
    }

    /// <summary>
    /// Verifies that the SHA-256 verification step does not cause a significant
    /// throughput regression: 10 MB transfer with hash verify must exceed 20 MB/s.
    /// </summary>
    [Fact]
    public async Task TransferThroughput_10MB_WithSha256_Exceeds20MBps()
    {
        const int size10MB = 10 * OneMB;
        var fileData = new byte[size10MB];
        Random.Shared.NextBytes(fileData);

        // Warm up
        await RunLoopbackTransferAsync(new byte[256 * 1024]);

        var elapsed = await RunLoopbackTransferAsync(fileData);

        double mbps = (size10MB / (double)OneMB) / elapsed.TotalSeconds;
        Console.WriteLine($"[TransferThroughput] 10 MB (SHA-256) in {elapsed.TotalMilliseconds:F0} ms → {mbps:F1} MB/s");

        Assert.True(mbps >= 20.0,
            $"10 MB transfer throughput {mbps:F1} MB/s is below 20 MB/s minimum.");
    }

    /// <summary>
    /// Measures per-chunk overhead by timing a multi-chunk transfer and asserting
    /// that per-chunk overhead is reasonable.
    /// </summary>
    [Fact]
    public async Task TransferThroughput_MultipleChunks_PerChunkOverheadIsNegligible()
    {
        // 512 KB = 8 chunks at the default 64 KB chunk size
        const int size = 512 * 1024;
        var fileData = new byte[size];
        Random.Shared.NextBytes(fileData);

        var elapsed = await RunLoopbackTransferAsync(fileData);

        // With 8 chunks, per-chunk overhead should not dominate
        int expectedChunks = size / TransferSession.ChunkSize + (size % TransferSession.ChunkSize > 0 ? 1 : 0);
        double msPerChunk  = elapsed.TotalMilliseconds / expectedChunks;

        Console.WriteLine($"[TransferThroughput] {expectedChunks} chunks, {msPerChunk:F2} ms/chunk");

        Assert.True(msPerChunk < 50.0,
            $"Per-chunk overhead {msPerChunk:F2} ms exceeds 50 ms limit — possible protocol regression.");
    }
}
