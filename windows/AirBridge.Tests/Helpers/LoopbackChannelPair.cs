using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
using System.Threading.Channels;

namespace AirBridge.Tests.Helpers;

/// <summary>
/// A pair of in-memory <see cref="IMessageChannel"/> instances wired so that
/// messages sent on <see cref="A"/> are received on <see cref="B"/> and vice versa.
/// No network sockets are used — tests run fast and deterministically.
/// </summary>
public sealed class LoopbackChannelPair
{
    public IMessageChannel A { get; }
    public IMessageChannel B { get; }

    private LoopbackChannelPair(FakeChannel a, FakeChannel b)
    {
        A = a;
        B = b;
    }

    /// <summary>Creates a connected pair with the given device-ID labels.</summary>
    public static LoopbackChannelPair Create(string idA = "device-a", string idB = "device-b")
    {
        // aToB: messages sent by A are read by B
        var aToB = Channel.CreateUnbounded<ProtocolMessage>(new UnboundedChannelOptions { SingleReader = true });
        // bToA: messages sent by B are read by A
        var bToA = Channel.CreateUnbounded<ProtocolMessage>(new UnboundedChannelOptions { SingleReader = true });

        var a = new FakeChannel(remoteDeviceId: idB, inbox: bToA.Reader, outbox: aToB.Writer);
        var b = new FakeChannel(remoteDeviceId: idA, inbox: aToB.Reader, outbox: bToA.Writer);
        return new LoopbackChannelPair(a, b);
    }

    // ── Inner implementation ───────────────────────────────────────────────

    private sealed class FakeChannel(
        string remoteDeviceId,
        ChannelReader<ProtocolMessage> inbox,
        ChannelWriter<ProtocolMessage> outbox) : IMessageChannel
    {
        public string RemoteDeviceId { get; } = remoteDeviceId;
        public string RemoteDeviceType { get; } = string.Empty;
        public bool IsConnected => !_closed;
        private bool _closed;

        public event EventHandler? Disconnected;

        public async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
        {
            if (_closed) throw new InvalidOperationException("Channel is closed");
            await outbox.WriteAsync(message, cancellationToken);
        }

        public async Task<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await inbox.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            _closed = true;
            outbox.TryComplete();
            Disconnected?.Invoke(this, EventArgs.Empty);
            return ValueTask.CompletedTask;
        }
    }
}
