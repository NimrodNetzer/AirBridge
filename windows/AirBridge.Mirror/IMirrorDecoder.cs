namespace AirBridge.Mirror;

/// <summary>
/// Abstraction over the H.264 decode pipeline.
/// Implemented by <see cref="MirrorDecoder"/>; a no-op stub is used in unit tests.
/// </summary>
public interface IMirrorDecoder : IDisposable
{
    /// <summary>
    /// Initializes the decoder at the given resolution.
    /// Must be called before any <see cref="SubmitNalUnit"/> calls.
    /// </summary>
    Task InitializeAsync(int width, int height);

    /// <summary>
    /// Submits one H.264 NAL unit for decoding.
    /// Returns a <see cref="DecodeResult"/> indicating whether the NAL was accepted.
    /// </summary>
    DecodeResult SubmitNalUnit(byte[] nalData, long timestampMs, bool isKeyFrame);
}
