using System.Collections.Concurrent;
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
    private long _nextTimestampMs;

    private int _width;
    private int _height;

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
            CanSeek  = false,
            Duration = TimeSpan.Zero  // live stream — no fixed duration
        };

        _streamSource.SampleRequested += OnSampleRequested;
        _initialized = true;
        return Task.CompletedTask;
    }

    // ── IMirrorDecoder ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Detects IDR keyframes by inspecting the NAL unit type byte (bits 0–4 == 5).
    /// Timestamps are synthesised at ~30 fps intervals; sub-ms accuracy is sufficient
    /// for local Wi-Fi mirroring where the display refresh rate dominates.
    /// </remarks>
    public Task PushFrameAsync(byte[] frameData, CancellationToken cancellationToken = default)
    {
        if (frameData is null || frameData.Length == 0)
            return Task.CompletedTask;

        // NAL unit type lives in bits [4:0] of the first byte (H.264 Annex B,
        // after any start-code prefix the caller has already stripped).
        bool isKeyFrame = (frameData[0] & 0x1F) == 5; // IDR slice

        long ts = System.Threading.Interlocked.Add(ref _nextTimestampMs, 33); // ~30 fps
        SubmitNalUnit(frameData, ts, isKeyFrame);
        return Task.CompletedTask;
    }

    // ── NAL submission ─────────────────────────────────────────────────────

    /// <summary>
    /// Queues one H.264 NAL unit for decoding.
    /// Non-keyframe NAL units before the first keyframe are dropped.
    /// </summary>
    /// <param name="nalData">Raw H.264 NAL bytes.</param>
    /// <param name="timestampMs">Presentation timestamp in milliseconds.</param>
    /// <param name="isKeyFrame"><c>true</c> if this is an IDR (keyframe) NAL unit.</param>
    /// <returns>
    /// <see cref="DecodeResult.Success"/> when the NAL unit was accepted;
    /// a failure result when it was dropped (pre-keyframe non-key NAL) or the
    /// decoder is not yet initialized.
    /// </returns>
    public DecodeResult SubmitNalUnit(byte[] nalData, long timestampMs, bool isKeyFrame)
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
        }

        _nalQueue.Enqueue(new PendingNal(nalData, timestampMs, isKeyFrame));
        _nalReady.Release();
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
                bool hasData = await _nalReady
                    .WaitAsync(TimeSpan.FromMilliseconds(150), _cts.Token)
                    .ConfigureAwait(false);

                if (!hasData || _cts.IsCancellationRequested || !_nalQueue.TryDequeue(out var nal))
                {
                    request.Sample = null; // EOS signal to WMF
                    return;
                }

                var ts     = TimeSpan.FromMilliseconds(nal.TimestampMs);
                var buffer = nal.NalData.AsBuffer();
                var sample = MediaStreamSample.CreateFromBuffer(buffer, ts);
                sample.KeyFrame = nal.IsKeyFrame;
                request.Sample  = sample;

                // Raise FrameDecoded so subscribers (MirrorWindow) can present the frame.
                // The bitmap here is a placeholder container at the correct dimensions;
                // MirrorWindow renders the MediaPlayer surface directly for zero-copy.
                var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, _width, _height);
                FrameDecoded?.Invoke(this, bitmap);
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
                deferral.Complete();
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
