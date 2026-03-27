using AirBridge.Core.Interfaces;
using AirBridge.Mirror;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
using NSubstitute;

namespace AirBridge.Tests.Mirror;

/// <summary>
/// State-machine tests for <see cref="MirrorSession"/>.
///
/// Uses an <see cref="IMessageChannel"/> substitute with a controlled message queue
/// so no real network sockets are needed.
///
/// All tests run headless: a no-op <see cref="IMirrorDecoder"/> stub is injected so
/// no WinRT decode pipeline is created, and <c>windowFactory = null</c> suppresses
/// any window construction.
/// </summary>
public class MirrorSessionTests
{
    // ── Test doubles ───────────────────────────────────────────────────────

    /// <summary>
    /// No-op <see cref="IMirrorDecoder"/> stub — records calls but does nothing.
    /// </summary>
    private sealed class StubDecoder : IMirrorDecoder
    {
        public int  SubmitCount { get; private set; }
        public bool Disposed    { get; private set; }

        public event EventHandler? FrameReady;

        public Task PushFrameAsync(byte[] frameData, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            FrameReady?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Disposed = true;
            FrameReady = null;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a mock <see cref="IMessageChannel"/> whose <c>ReceiveAsync</c> returns
    /// <paramref name="messages"/> in sequence, then returns <c>null</c> (clean close).
    /// </summary>
    private static IMessageChannel BuildChannel(params ProtocolMessage[] messages)
    {
        var channel = Substitute.For<IMessageChannel>();
        channel.IsConnected.Returns(true);
        channel.RemoteDeviceId.Returns("test-android");
        channel.DisposeAsync().Returns(new ValueTask());

        var queue = new Queue<ProtocolMessage?>(messages);
        queue.Enqueue(null); // clean channel close

        channel.ReceiveAsync(Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   queue.TryDequeue(out var msg);
                   return Task.FromResult(msg);
               });

        return channel;
    }

    private static ProtocolMessage Msg(MessageType type, byte[] payload) =>
        new(type, payload);

    private static MirrorSession BuildSession(
        string sessionId,
        IMessageChannel channel,
        StubDecoder? decoder = null) =>
        new MirrorSession(
            sessionId,
            channel,
            decoderFactory: () => decoder ?? new StubDecoder(),
            windowFactory:  null);  // headless — no WinUI 3 window

    // ── MirrorStart handling ───────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ReceivesMirrorStart_TransitionsToActive()
    {
        var startMsg = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H264, 1920, 1080, 30, "sid-1");
        var channel  = BuildChannel(Msg(MessageType.MirrorStart, startMsg.ToBytes()));

        var stateHistory = new List<MirrorState>();
        var session = BuildSession("sid-1", channel);
        session.StateChanged += (_, s) => stateHistory.Add(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        Assert.Contains(MirrorState.Active, stateHistory);
    }

    [Fact]
    public async Task StartAsync_ReceivesMirrorStartThenFrame_DecoderReceivesFrame()
    {
        var stub     = new StubDecoder();
        var startMsg = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H264, 1920, 1080, 30, "sid-init");
        var frameMsg = new MirrorFrameMessage(true, 1000L, new byte[] { 0, 0, 0, 1, 0x65 });
        var channel  = BuildChannel(
            Msg(MessageType.MirrorStart, startMsg.ToBytes()),
            Msg(MessageType.MirrorFrame, frameMsg.ToBytes()));
        var session  = BuildSession("sid-init", channel, stub);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        Assert.Equal(1, stub.SubmitCount);
    }

    [Fact]
    public async Task StartAsync_SessionIdMatchesConstructorArg()
    {
        const string SessionId = "my-session";
        var startMsg = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H264, 1280, 720, 30, SessionId);
        var channel  = BuildChannel(Msg(MessageType.MirrorStart, startMsg.ToBytes()));
        var session  = BuildSession(SessionId, channel);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        Assert.Equal(SessionId, session.SessionId);
    }

    // ── MirrorFrame handling ───────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ReceivesFrame_ForwardsToDecoder()
    {
        var stub     = new StubDecoder();
        var startMsg = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H264, 1920, 1080, 30, "sid-frame");
        var frameMsg = new MirrorFrameMessage(true, 1000L, new byte[] { 0, 0, 0, 1, 0x65 });

        var channel = BuildChannel(
            Msg(MessageType.MirrorStart, startMsg.ToBytes()),
            Msg(MessageType.MirrorFrame, frameMsg.ToBytes()));

        var session = BuildSession("sid-frame", channel, stub);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        Assert.Equal(1, stub.SubmitCount);
    }

    [Fact]
    public async Task StartAsync_ReceivesFrameWithoutStart_ForwardsToDecoder()
    {
        var stub     = new StubDecoder();
        var frameMsg = new MirrorFrameMessage(true, 0L, new byte[] { 1, 2, 3 });
        var channel  = BuildChannel(Msg(MessageType.MirrorFrame, frameMsg.ToBytes()));
        var session  = BuildSession("sid-early", channel, stub);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        Assert.Equal(1, stub.SubmitCount);
    }

    // ── MirrorStop handling ────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ReceivesMirrorStop_TransitionsToStopped()
    {
        var startMsg = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H264, 1920, 1080, 30, "sid-stop");
        var stopMsg  = new MirrorStopMessage();

        var channel = BuildChannel(
            Msg(MessageType.MirrorStart, startMsg.ToBytes()),
            Msg(MessageType.MirrorStop,  stopMsg.ToBytes()));

        var stateHistory = new List<MirrorState>();
        var session = BuildSession("sid-stop", channel);
        session.StateChanged += (_, s) => stateHistory.Add(s);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        Assert.Contains(MirrorState.Stopped, stateHistory);
    }

    // ── StopAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_CancelsReceiveLoop()
    {
        // Channel that blocks indefinitely
        var channel = Substitute.For<IMessageChannel>();
        channel.IsConnected.Returns(true);
        channel.RemoteDeviceId.Returns("peer");
        channel.DisposeAsync().Returns(new ValueTask());
        channel.ReceiveAsync(Arg.Any<CancellationToken>())
               .Returns(async callInfo =>
               {
                   var ct = (CancellationToken)callInfo[0];
                   await Task.Delay(Timeout.Infinite, ct);
                   return (ProtocolMessage?)null;
               });

        var session   = BuildSession("sid-cancel", channel);
        var startTask = session.StartAsync();

        await Task.Delay(20);
        await session.StopAsync();

        var completed = await Task.WhenAny(startTask, Task.Delay(3000));
        Assert.Same(startTask, completed);
        Assert.Equal(MirrorState.Stopped, session.State);
    }

    // ── Channel clean close ────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_ChannelClosesCleanly_TransitionsToStopped()
    {
        var channel = BuildChannel(); // no messages — immediate null
        var session = BuildSession("sid-close", channel);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        Assert.Equal(MirrorState.Stopped, session.State);
    }

    // ── Mode ───────────────────────────────────────────────────────────────

    [Fact]
    public void Mode_IsPhoneWindow()
    {
        var channel = BuildChannel();
        var session = BuildSession("sid-mode", channel);
        Assert.Equal(MirrorMode.PhoneWindow, session.Mode);
    }

    // ── Dispose ────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var channel = BuildChannel();
        var session = BuildSession("sid-dispose", channel);
        var ex = Record.Exception(() => session.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_DisposesDecoder()
    {
        var stub     = new StubDecoder();
        var startMsg = new MirrorStartMessage(MirrorSessionMode.PhoneWindow, MirrorCodec.H264, 640, 480, 30, "sid-disp");
        var channel  = BuildChannel(Msg(MessageType.MirrorStart, startMsg.ToBytes()));
        var session  = BuildSession("sid-disp", channel, stub);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.StartAsync(cts.Token);

        // After StartAsync completes (channel closed) cleanup should have disposed the decoder
        Assert.True(stub.Disposed);
    }

    // ── SendInputAsync (Iteration 5 no-op) ────────────────────────────────

    [Fact]
    public async Task SendInputAsync_DoesNotThrow_InIteration5()
    {
        var channel = BuildChannel();
        var session = BuildSession("sid-input", channel);
        var evt     = new InputEventArgs(InputEventType.Touch, 0.5f, 0.5f);

        var ex = await Record.ExceptionAsync(
            () => session.SendInputAsync(evt));
        Assert.Null(ex);
    }
}
