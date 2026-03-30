namespace AirBridge.Mirror;

/// <summary>
/// Decodes an incoming H.264/H.265 byte stream into rendered frames.
/// Implemented in the WinUI 3 App layer using Windows Media Foundation
/// or Direct3D surfaces; mocked in unit tests.
/// </summary>
public interface IMirrorDecoder : IDisposable
{
    /// <summary>
    /// Initializes the decode pipeline with the stream resolution.
    /// Must be called before any <see cref="PushFrameAsync"/> calls.
    /// </summary>
    Task InitializeAsync(int width, int height);

    /// <summary>Feeds a raw encoded NAL unit into the decoder pipeline.</summary>
    /// <param name="nalData">Raw H.264 NAL bytes (Annex B, as produced by Android MediaCodec).</param>
    /// <param name="isKeyFrame">True if this NAL unit is an IDR keyframe.</param>
    /// <param name="timestampUs">Presentation timestamp in microseconds from the encoder.</param>
    /// <param name="cancellationToken">Token to cancel the decode operation.</param>
    Task PushFrameAsync(byte[] nalData, bool isKeyFrame, long timestampUs, CancellationToken cancellationToken = default);

    /// <summary>Raised when a decoded frame is ready for rendering.</summary>
    event EventHandler? FrameReady;
}
