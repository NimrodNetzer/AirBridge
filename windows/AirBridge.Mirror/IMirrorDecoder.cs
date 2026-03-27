namespace AirBridge.Mirror;

/// <summary>
/// Decodes an incoming H.264/H.265 byte stream into rendered frames.
/// Implemented in the WinUI 3 App layer using Windows Media Foundation
/// or Direct3D surfaces; mocked in unit tests.
/// </summary>
public interface IMirrorDecoder : IDisposable
{
    /// <summary>Feeds a raw encoded frame into the decoder pipeline.</summary>
    /// <param name="frameData">Encoded video frame bytes.</param>
    /// <param name="cancellationToken">Token to cancel the decode operation.</param>
    Task PushFrameAsync(byte[] frameData, CancellationToken cancellationToken = default);

    /// <summary>Raised when a decoded frame is ready for rendering.</summary>
    event EventHandler? FrameReady;
}
