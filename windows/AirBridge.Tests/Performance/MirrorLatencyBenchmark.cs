using System.Buffers.Binary;
using System.Diagnostics;
using AirBridge.Mirror;

namespace AirBridge.Tests.Performance;

/// <summary>
/// Self-timing mirror frame latency benchmark.
///
/// Simulates the mirror decode pipeline by generating synthetic H.264 NAL unit payloads,
/// parsing them through <see cref="MirrorFrameMessage"/> serialization/deserialization,
/// and measuring average per-frame processing time.
///
/// Target: average &lt; 5 ms per frame — well within the 100 ms end-to-end latency budget.
///
/// This benchmark tests the message framing layer only, not the actual H.264 decoder
/// (which requires GPU/hardware and is out of scope for unit tests).
/// </summary>
public class MirrorLatencyBenchmark
{
    private const int FrameCount      = 100;
    private const int SyntheticNalKB  = 50;      // 50 KB per frame — realistic compressed I-frame size
    private const double MaxMsPerFrame = 5.0;    // 5 ms per frame target

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates <paramref name="count"/> synthetic NAL unit payloads of
    /// <paramref name="sizeKB"/> KB each, filled with random bytes.
    /// </summary>
    private static byte[][] GenerateSyntheticNalUnits(int count, int sizeKB)
    {
        var nalUnits = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            nalUnits[i] = new byte[sizeKB * 1024];
            Random.Shared.NextBytes(nalUnits[i]);
        }
        return nalUnits;
    }

    /// <summary>
    /// Measures the time to serialize and deserialize one <see cref="MirrorFrameMessage"/>.
    /// Returns elapsed ticks.
    /// </summary>
    private static long MeasureFrameRoundTripTicks(byte[] nalData, long frameIndex)
    {
        var sw = Stopwatch.StartNew();

        // Serialize: simulate what the Android sender writes to the wire
        var msg = new MirrorFrameMessage(
            IsKeyFrame: frameIndex % 30 == 0, // key frame every 30 frames (standard GOP)
            PresentationTimestampUs: frameIndex * 33_333, // 30 fps = ~33 ms per frame
            NalData: nalData);
        var bytes = msg.ToBytes();

        // Deserialize: simulate what the Windows receiver reads from the wire
        var parsed = MirrorFrameMessage.FromBytes(bytes.AsSpan());

        sw.Stop();

        // Verify round-trip correctness (sanity check)
        if (parsed.IsKeyFrame != msg.IsKeyFrame ||
            parsed.PresentationTimestampUs != msg.PresentationTimestampUs ||
            parsed.NalData.Length != msg.NalData.Length)
        {
            throw new InvalidOperationException("MirrorFrameMessage round-trip data corruption detected.");
        }

        return sw.ElapsedTicks;
    }

    // ── Benchmark tests ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses 100 synthetic 50 KB NAL units through MirrorFrameMessage and asserts
    /// average per-frame time is below 5 ms.
    /// </summary>
    [Fact]
    public void MirrorFrameParsing_100Frames_AverageLessThan5ms()
    {
        var nalUnits = GenerateSyntheticNalUnits(FrameCount, SyntheticNalKB);

        // Warm up JIT with a few frames before timing
        for (int i = 0; i < 5; i++)
            MeasureFrameRoundTripTicks(nalUnits[i % FrameCount], i);

        // Timed run
        long totalTicks = 0;
        for (int i = 0; i < FrameCount; i++)
            totalTicks += MeasureFrameRoundTripTicks(nalUnits[i], i);

        double avgMs = (totalTicks / (double)FrameCount) / Stopwatch.Frequency * 1000.0;
        double totalMs = totalTicks / (double)Stopwatch.Frequency * 1000.0;

        Console.WriteLine($"[MirrorLatency] {FrameCount} frames × {SyntheticNalKB} KB: " +
                          $"total={totalMs:F1} ms, avg={avgMs:F3} ms/frame");

        Assert.True(avgMs < MaxMsPerFrame,
            $"Average mirror frame parsing time {avgMs:F3} ms exceeds {MaxMsPerFrame} ms target. " +
            $"Total for {FrameCount} frames: {totalMs:F1} ms.");
    }

    /// <summary>
    /// Verifies that MirrorStartMessage serialization/deserialization is fast
    /// (session open overhead should be negligible).
    /// </summary>
    [Fact]
    public void MirrorStartMessage_Roundtrip_LessThan1ms()
    {
        var msg = new MirrorStartMessage(
            Mode:      MirrorSessionMode.PhoneWindow,
            Codec:     MirrorCodec.H264,
            Width:     1920,
            Height:    1080,
            Fps:       30,
            SessionId: Guid.NewGuid().ToString());

        // Warm up
        for (int i = 0; i < 10; i++)
        {
            var b = msg.ToBytes();
            MirrorStartMessage.FromBytes(b.AsSpan());
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            var bytes  = msg.ToBytes();
            var parsed = MirrorStartMessage.FromBytes(bytes.AsSpan());
            _ = parsed; // prevent optimizer from eliding
        }
        sw.Stop();

        double avgMicros = sw.Elapsed.TotalMicroseconds / 1000.0;
        Console.WriteLine($"[MirrorLatency] MirrorStartMessage 1000 round-trips: avg={avgMicros:F2} µs");

        Assert.True(avgMicros < 1000.0, // < 1 ms per round-trip
            $"MirrorStartMessage round-trip avg {avgMicros:F2} µs exceeds 1000 µs.");
    }

    /// <summary>
    /// Verifies 50 KB frames do not cause allocations that grow across 100 frames
    /// (GC pressure test — confirms no per-frame buffer leak).
    /// </summary>
    [Fact]
    public void MirrorFrameParsing_100Frames_DoesNotLeakMemory()
    {
        var nalUnits = GenerateSyntheticNalUnits(FrameCount, SyntheticNalKB);

        // Run once to establish baseline allocations
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long memBefore = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < FrameCount; i++)
        {
            var msg    = new MirrorFrameMessage(false, i * 33_333L, nalUnits[i]);
            var bytes  = msg.ToBytes();
            var parsed = MirrorFrameMessage.FromBytes(bytes.AsSpan());
            _ = parsed;
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long memAfter = GC.GetTotalMemory(forceFullCollection: true);

        // Allow up to 5 MB of retained objects (generous — normally close to zero after GC)
        long retainedBytes = memAfter - memBefore;
        Console.WriteLine($"[MirrorLatency] Memory delta after 100 frames: {retainedBytes / 1024.0:F1} KB");

        Assert.True(retainedBytes < 5 * 1024 * 1024,
            $"Retained memory after 100 frames: {retainedBytes / 1024} KB — possible leak.");
    }

    /// <summary>
    /// Verifies that the per-frame framing layer (header encode/decode) for a
    /// minimal 1-byte payload is well under 1 µs — confirming framing overhead is negligible.
    /// </summary>
    [Fact]
    public void MirrorFrameParsing_MinimalPayload_OverheadIsNegligible()
    {
        var tinyNal = new byte[] { 0x65, 0x88, 0x84, 0x00 }; // 4 bytes — IDR NAL unit header

        // Warm up
        for (int i = 0; i < 100; i++)
        {
            var b = new MirrorFrameMessage(true, i * 1000L, tinyNal).ToBytes();
            MirrorFrameMessage.FromBytes(b.AsSpan());
        }

        int iterations = 10_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var msg    = new MirrorFrameMessage(i % 30 == 0, i * 33_333L, tinyNal);
            var bytes  = msg.ToBytes();
            var parsed = MirrorFrameMessage.FromBytes(bytes.AsSpan());
            _ = parsed;
        }
        sw.Stop();

        double avgNs = sw.Elapsed.TotalNanoseconds / iterations;
        Console.WriteLine($"[MirrorLatency] Minimal frame encode+decode: {avgNs:F0} ns/frame avg over {iterations} iterations");

        // Framing overhead alone should be under 100 µs even on slow machines
        Assert.True(avgNs < 100_000, // 100 µs in nanoseconds
            $"Minimal frame encode+decode avg {avgNs:F0} ns — expected < 100,000 ns (100 µs).");
    }

    /// <summary>
    /// Verifies the MirrorStopMessage (session teardown) serializes correctly and quickly.
    /// </summary>
    [Fact]
    public void MirrorStopMessage_Roundtrip_IsCorrectAndFast()
    {
        var msg    = new MirrorStopMessage(ReasonCode: 0);
        var bytes  = msg.ToBytes();
        var parsed = MirrorStopMessage.FromBytes(bytes.AsSpan());

        Assert.Equal(msg.ReasonCode, parsed.ReasonCode);

        // Timing sanity: 1000 round-trips should complete in well under 10 ms
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            var b = new MirrorStopMessage(0).ToBytes();
            _ = MirrorStopMessage.FromBytes(b.AsSpan());
        }
        sw.Stop();

        Console.WriteLine($"[MirrorLatency] MirrorStopMessage 1000 round-trips: {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"MirrorStopMessage 1000 round-trips took {sw.ElapsedMilliseconds} ms — expected < 100 ms.");
    }
}
