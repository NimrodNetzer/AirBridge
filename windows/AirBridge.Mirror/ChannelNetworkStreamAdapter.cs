using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;

namespace AirBridge.Mirror;

/// <summary>
/// Adapts an <see cref="IMessageChannel"/> into a writable <see cref="Stream"/>
/// so that <c>AirBridge.Transfer.TransferSession</c> (which expects a raw
/// <see cref="Stream"/> for its network output) can send file-transfer protocol
/// messages over the existing mirror channel.
/// <para>
/// Each call to <see cref="WriteAsync(ReadOnlyMemory{byte},CancellationToken)"/>
/// wraps the supplied bytes in a <see cref="MessageType.FileChunk"/> protocol
/// envelope and sends it via <see cref="IMessageChannel.SendAsync"/>.
/// </para>
/// <para>
/// This adapter is write-only; read operations are not supported.
/// </para>
/// </summary>
internal sealed class ChannelNetworkStreamAdapter : Stream
{
    private readonly IMessageChannel _channel;
    private readonly CancellationToken _ct;
    private bool _disposed;

    /// <summary>
    /// Initialises the adapter.
    /// </summary>
    /// <param name="channel">The underlying transport channel.</param>
    /// <param name="ct">
    ///   Token that cancels any pending write operations.
    ///   Typically the mirror session's cancellation token.
    /// </param>
    public ChannelNetworkStreamAdapter(IMessageChannel channel, CancellationToken ct)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _ct = ct;
    }

    // ── Stream capabilities ────────────────────────────────────────────────

    /// <inheritdoc/>
    public override bool CanRead  => false;

    /// <inheritdoc/>
    public override bool CanSeek  => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    // ── Write ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends <paramref name="buffer"/> as a raw-payload
    /// <see cref="MessageType.FileChunk"/> message on the channel.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        var payload = buffer.AsSpan(offset, count).ToArray();
        // Fire-and-forget synchronous bridge — use the async path where possible.
        _channel.SendAsync(new ProtocolMessage(MessageType.FileChunk, payload), _ct)
                .GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
        var payload = buffer.ToArray();
        await _channel.SendAsync(
            new ProtocolMessage(MessageType.FileChunk, payload), linked.Token)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override void Flush() { /* no buffering */ }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── Unsupported ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("ChannelNetworkStreamAdapter is write-only.");

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value)
        => throw new NotSupportedException();

    // ── Dispose ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            // Channel lifetime is managed by the MirrorSession; do not close it here.
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
