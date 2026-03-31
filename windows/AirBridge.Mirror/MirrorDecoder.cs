using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace AirBridge.Mirror;

/// <summary>
/// Result of a decode operation.
/// </summary>
public sealed record DecodeResult(bool IsSuccess, string? ErrorMessage = null)
{
    /// <summary>Singleton success result.</summary>
    public static readonly DecodeResult Success = new(true);
    /// <summary>Creates a failure result with the given message.</summary>
    public static DecodeResult Failure(string message) => new(false, message);
}

/// <summary>
/// Wraps the Windows Media Foundation H.264 decode pipeline using
/// <see cref="MediaStreamSource"/> so that encoded NAL units can be fed
/// incrementally and decoded frames are surfaced via <see cref="FrameDecoded"/>.
///
/// <para>
/// Usage:
/// <list type="number">
///   <item>Subscribe to <see cref="FrameDecoded"/> to receive decoded <see cref="SoftwareBitmap"/> frames.</item>
///   <item>Call <see cref="InitializeAsync"/> with the stream resolution.</item>
///   <item>Call <see cref="SubmitNalUnit"/> for each incoming H.264 NAL buffer.</item>
///   <item>Dispose when done.</item>
/// </list>
/// </para>
///
/// <para>
/// Non-keyframe NAL units received before the first keyframe are silently dropped to
/// ensure the decoder can initialise from a clean sync point.
/// </para>
/// </summary>
public sealed class MirrorDecoder : IMirrorDecoder
{
    // ── Events ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a frame has been decoded. The <see cref="SoftwareBitmap"/> is in
    /// <see cref="BitmapPixelFormat.Bgra8"/> format. Callers must dispose the bitmap after use.
    /// </summary>
    public event EventHandler<SoftwareBitmap>? FrameDecoded;

    /// <inheritdoc/>
    public event EventHandler? FrameReady;

    // ── State ──────────────────────────────────────────────────────────────

    private MediaStreamSource?        _streamSource;
    private VideoStreamDescriptor?    _videoDescriptor;

    private readonly ConcurrentQueue<PendingNal> _nalQueue    = new();
    private readonly SemaphoreSlim               _nalReady    = new(0);
    private readonly CancellationTokenSource     _cts         = new();

    private bool _seenKeyFrame;
    private bool _initialized;
    private bool _disposed;

    private int  _width;
    private int  _height;

    // ── Timestamp strategy ──────────────────────────────────────────────────
    // Use wall-clock arrival time, normalized to the first IDR keyframe = 0ms.
    //
    // Why not encoder PTS: the Snapdragon encoder resets PTS to 0 on each cycling
    // restart, and frames burst off in clusters — PTS values are meaningless for
    // scheduling live content.
    //
    // Why not raw Stopwatch from construction: the decoder is created before the
    // mirror session starts.  The first IDR might arrive 146 s later (ts=146371ms).
    // WMF's MediaPlayer clock starts at 0 on Play() and advances in real time —
    // a sample at ts=146371ms wouldn't be shown for 146 seconds.
    //
    // Solution: record the Stopwatch value at the first IDR and subtract it from
    // every subsequent timestamp.  First IDR = ts 0ms, frames after it = elapsed
    // ms since that IDR.  WMF shows the first IDR immediately and subsequent frames
    // at their natural arrival cadence — no scheduling wait, no stuck frames.
    private readonly Stopwatch _arrivalClock    = Stopwatch.StartNew();
    private          long      _firstIdrMs      = -1L;   // Stopwatch ms at first IDR
    private          long      _lastDeliveredMs = -1L;

    // ── Frame-drop threshold ────────────────────────────────────────────────
    // Keep a small queue (4 frames ≈ 133 ms at 30 fps).  With wall-clock
    // timestamps every frame is immediately due, so a large queue would only
    // cause WMF to blast through stale frames before showing live content.
    private const int MaxQueueDepth = 4;

    // ── Initialization ─────────────────────────────────────────────────────

    /// <summary>
    /// Initializes the Media Foundation H.264 decode pipeline.
    /// Must be called before any <see cref="SubmitNalUnit"/> calls.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    public Task InitializeAsync(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _width  = width;
        _height = height;

        var encProps = VideoEncodingProperties.CreateH264();
        encProps.Width  = (uint)width;
        encProps.Height = (uint)height;

        _videoDescriptor = new VideoStreamDescriptor(encProps);
        _streamSource    = new MediaStreamSource(_videoDescriptor)
        {
            CanSeek    = false,
            BufferTime = TimeSpan.Zero,  // minimize latency — don't pre-buffer for a live stream
            // Duration intentionally not set: null = unbounded live stream.
            // Setting Duration = TimeSpan.Zero would tell WMF "0-second stream" and cause
            // it to stop pulling samples immediately, resulting in a black window.
        };

        _streamSource.SampleRequested += OnSampleRequested;
        _initialized = true;
        return Task.CompletedTask;
    }

    // ── IMirrorDecoder ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task PushFrameAsync(byte[] nalData, bool isKeyFrame, long timestampUs, CancellationToken cancellationToken = default)
    {
        if (nalData is null || nalData.Length == 0)
            return Task.CompletedTask;

        SubmitNalUnit(nalData, timestampUs, isKeyFrame);
        return Task.CompletedTask;
    }

    // ── NAL submission ─────────────────────────────────────────────────────

    /// <summary>
    /// Queues one H.264 NAL unit for decoding.
    /// Non-keyframe NAL units before the first keyframe are dropped.
    /// </summary>
    /// <param name="nalData">Raw H.264 NAL bytes.</param>
    /// <param name="timestampUs">Presentation timestamp in microseconds (from the encoder).</param>
    /// <param name="isKeyFrame"><c>true</c> if this is an IDR (keyframe) NAL unit.</param>
    /// <returns>
    /// <see cref="DecodeResult.Success"/> when the NAL unit was accepted;
    /// a failure result when it was dropped (pre-keyframe non-key NAL) or the
    /// decoder is not yet initialized.
    /// </returns>
    public DecodeResult SubmitNalUnit(byte[] nalData, long timestampUs, bool isKeyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            return DecodeResult.Failure("Decoder not initialized. Call InitializeAsync first.");
        if (nalData is null || nalData.Length == 0)
            return DecodeResult.Failure("NAL data must not be null or empty.");

        if (!_seenKeyFrame)
        {
            if (!isKeyFrame)
                return DecodeResult.Failure("Dropped: waiting for first keyframe.");
            _seenKeyFrame = true;
            AirBridge.Core.AppLog.Info($"[MirrorDecoder] First IDR keyframe queued ({nalData.Length}B)");
        }

        // On IDR: flush any queued frames — stale content from before this keyframe.
        if (isKeyFrame && _seenKeyFrame)
        {
            int stale = 0;
            while (_nalQueue.TryDequeue(out _)) stale++;
            for (int i = 0; i < stale; i++) _nalReady.Wait(0);
            if (stale > 0)
                AirBridge.Core.AppLog.Info($"[MirrorDecoder] IDR: flushed {stale} stale frames");
        }

        // Anchor the clock to the first IDR so ts=0 aligns with when WMF's
        // MediaPlayer starts playing, regardless of when the decoder was created.
        var absoluteMs = _arrivalClock.ElapsedMilliseconds;
        if (isKeyFrame && _firstIdrMs < 0)
            _firstIdrMs = absoluteMs;

        var arrivalMs = _firstIdrMs < 0
            ? 0L
            : absoluteMs - _firstIdrMs;

        if (arrivalMs <= _lastDeliveredMs) arrivalMs = _lastDeliveredMs + 1L;
        _lastDeliveredMs = arrivalMs;

        _nalQueue.Enqueue(new PendingNal(nalData, arrivalMs, isKeyFrame));
        _nalReady.Release();

        // Drop oldest non-IDR frames when the queue grows too deep.
        if (_nalQueue.Count > MaxQueueDepth)
        {
            int dropped = 0;
            while (_nalQueue.Count > MaxQueueDepth &&
                   _nalQueue.TryPeek(out var oldest) && !oldest.IsKeyFrame)
            {
                if (_nalQueue.TryDequeue(out _)) dropped++;
            }
            for (int i = 0; i < dropped; i++) _nalReady.Wait(0);
        }

        return DecodeResult.Success;
    }

    /// <summary>
    /// Exposes the underlying <see cref="MediaStreamSource"/> so that callers can
    /// attach it to a <see cref="Windows.Media.Playback.MediaPlayer"/> for GPU-accelerated
    /// rendering inside a <see cref="Windows.UI.Xaml.Controls.SwapChainPanel"/>.
    /// Only valid after <see cref="InitializeAsync"/> has been called.
    /// </summary>
    public MediaStreamSource? GetMediaStreamSource() => _streamSource;

    // ── Private ────────────────────────────────────────────────────────────

    /// <summary>
    /// Callback invoked by Media Foundation when it is ready to consume the next sample.
    /// Dequeues one NAL unit (waiting up to 150 ms) and hands it to the pipeline.
    /// </summary>
    private void OnSampleRequested(
        MediaStreamSource sender,
        MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request  = args.Request;
        var deferral = request.GetDeferral();

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait indefinitely for the next NAL unit.  A short timeout would
                // send null (EOS) to WMF, which permanently stops sample requests.
                // We rely solely on _cts for clean shutdown.
                await _nalReady
                    .WaitAsync(_cts.Token)
                    .ConfigureAwait(false);

                if (_cts.IsCancellationRequested || !_nalQueue.TryDequeue(out var nal))
                {
                    request.Sample = null; // EOS on cancellation only
                    return;
                }

                var ts     = TimeSpan.FromMilliseconds(nal.TimestampMs);
                var buffer = nal.NalData.AsBuffer();
                var sample = MediaStreamSample.CreateFromBuffer(buffer, ts);
                sample.KeyFrame = nal.IsKeyFrame;
                request.Sample  = sample;

                if (nal.IsKeyFrame)
                    AirBridge.Core.AppLog.Info($"[MirrorDecoder] OnSampleRequested: delivered IDR keyframe ({nal.NalData.Length}B, ts={nal.TimestampMs}ms)");

                // MediaPlayer renders directly from the MediaStreamSource pipeline —
                // no external frame notification needed.
                FrameReady?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                request.Sample = null;
            }
            catch
            {
                request.Sample = null;
            }
            finally
            {
                try { deferral.Complete(); }
                catch { /* MSS may already be closed during teardown — safe to ignore */ }
            }
        });
    }

    // ── IDisposable ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _nalReady.Dispose();
        if (_streamSource is not null)
            _streamSource.SampleRequested -= OnSampleRequested;
    }

    // ── Private types ──────────────────────────────────────────────────────

    private readonly record struct PendingNal(byte[] NalData, long TimestampMs, bool IsKeyFrame);
}
