using AirBridge.Core.Interfaces;
using AirBridge.Mirror;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
using NSubstitute;

namespace AirBridge.Tests.Mirror;

/// <summary>
/// Unit tests for <see cref="TabletDisplaySession"/>.
///
/// Uses NSubstitute to mock <see cref="IMessageChannel"/>.
/// No real network connections or named pipes are created.
/// </summary>
public class TabletDisplaySessionTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IMessageChannel MakeChannel()
    {
        var ch = Substitute.For<IMessageChannel>();
        ch.IsConnected.Returns(true);
        ch.RemoteDeviceId.Returns("mock-android");
        ch.SendAsync(Arg.Any<ProtocolMessage>(), Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);
        return ch;
    }

    // ── MirrorMessage round-trip tests ─────────────────────────────────────

    [Fact]
    public void MirrorStartMessage_RoundTrip()
    {
        var msg     = new MirrorStartMessage(
            MirrorSessionMode.TabletDisplay,
            MirrorCodec.H264,
            2560, 1600, 60, "session-abc");
        var bytes   = msg.ToBytes();
        var decoded = MirrorStartMessage.FromBytes(bytes);

        Assert.Equal(msg.Mode,      decoded.Mode);
        Assert.Equal(msg.Codec,     decoded.Codec);
        Assert.Equal(msg.Width,     decoded.Width);
        Assert.Equal(msg.Height,    decoded.Height);
        Assert.Equal(msg.Fps,       decoded.Fps);
        Assert.Equal(msg.SessionId, decoded.SessionId);
    }

    [Fact]
    public void MirrorFrameMessage_RoundTrip_KeyFrame()
    {
        var nal     = new byte[] { 0x65, 0x88, 0x84, 0x00 }; // IDR NAL
        var msg     = new MirrorFrameMessage(true, 500_000L, nal);
        var bytes   = msg.ToBytes();
        var decoded = MirrorFrameMessage.FromBytes(bytes);

        Assert.True(decoded.IsKeyFrame);
        Assert.Equal(500_000L, decoded.PresentationTimestampUs);
        Assert.Equal(nal,      decoded.NalData);
    }

    [Fact]
    public void MirrorFrameMessage_RoundTrip_DeltaFrame()
    {
        var nal     = new byte[] { 0x41, 0x9A, 0x00 };
        var msg     = new MirrorFrameMessage(false, 1_016_666L, nal);
        var bytes   = msg.ToBytes();
        var decoded = MirrorFrameMessage.FromBytes(bytes);

        Assert.False(decoded.IsKeyFrame);
        Assert.Equal(1_016_666L, decoded.PresentationTimestampUs);
        Assert.Equal(nal,        decoded.NalData);
    }

    [Fact]
    public void MirrorStopMessage_RoundTrip_NormalReason()
    {
        var msg     = new MirrorStopMessage(0);
        var decoded = MirrorStopMessage.FromBytes(msg.ToBytes());
        Assert.Equal(0, decoded.ReasonCode);
    }

    [Fact]
    public void MirrorStopMessage_RoundTrip_ErrorReason()
    {
        var msg     = new MirrorStopMessage(1);
        var decoded = MirrorStopMessage.FromBytes(msg.ToBytes());
        Assert.Equal(1, decoded.ReasonCode);
    }

    // ── State machine tests ─────────────────────────────────────────────────

    [Fact]
    public void Session_InitialState_IsConnecting()
    {
        var ch      = MakeChannel();
        var session = new TabletDisplaySession("sid-1", ch);
        Assert.Equal(MirrorState.Connecting, session.State);
    }

    [Fact]
    public void Session_Mode_IsTabletDisplay()
    {
        var ch      = MakeChannel();
        var session = new TabletDisplaySession("sid-2", ch);
        Assert.Equal(MirrorMode.TabletDisplay, session.Mode);
    }

    [Fact]
    public void Session_SessionId_IsPreserved()
    {
        var ch      = MakeChannel();
        var session = new TabletDisplaySession("my-session-id", ch);
        Assert.Equal("my-session-id", session.SessionId);
    }

    [Fact]
    public async Task StartAsync_SendsMirrorStartMessage_BeforeConnectingToPipe()
    {
        // Arrange: channel that records sent messages
        var sent  = new List<ProtocolMessage>();
        var ch    = Substitute.For<IMessageChannel>();
        ch.IsConnected.Returns(true);
        ch.RemoteDeviceId.Returns("android");
        ch.SendAsync(
              Arg.Do<ProtocolMessage>(m => sent.Add(m)),
              Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var session = new TabletDisplaySession("sid-start", ch);

        // Act: StartAsync will send MirrorStart then attempt to open the named pipe.
        // Since the pipe doesn't exist in the test environment, it will throw after
        // sending the message.
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await session.StartAsync(cts.Token);
        }
        catch (Exception)
        {
            // Expected: pipe not found in test environment
        }

        // Assert: at least one message was sent before the failure
        Assert.NotEmpty(sent);
        Assert.Equal(MessageType.MirrorStart, sent[0].Type);
    }

    [Fact]
    public async Task StartAsync_SentMirrorStart_HasTabletDisplayMode()
    {
        // Arrange
        var sent = new List<ProtocolMessage>();
        var ch   = Substitute.For<IMessageChannel>();
        ch.IsConnected.Returns(true);
        ch.RemoteDeviceId.Returns("android");
        ch.SendAsync(
              Arg.Do<ProtocolMessage>(m => sent.Add(m)),
              Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var session = new TabletDisplaySession("sid-mode", ch);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await session.StartAsync(cts.Token);
        }
        catch (Exception) { }

        Assert.NotEmpty(sent);
        // Decode and verify mode
        var startPayload = sent[0].Payload;
        var startMsg     = MirrorStartMessage.FromBytes(startPayload);
        Assert.Equal(MirrorSessionMode.TabletDisplay, startMsg.Mode);
    }

    [Fact]
    public async Task StopAsync_WhenConnecting_TransitionsToStopped()
    {
        var ch      = MakeChannel();
        var session = new TabletDisplaySession("sid-stop", ch);

        // Stop without starting — should gracefully handle this
        await session.StopAsync();

        // StopAsync should send MirrorStop message
        await ch.Received().SendAsync(
            Arg.Is<ProtocolMessage>(m => m.Type == MessageType.MirrorStop),
            Arg.Any<CancellationToken>());

        Assert.Equal(MirrorState.Stopped, session.State);
    }

    [Fact]
    public async Task StopAsync_SendsMirrorStopMessage()
    {
        var sent = new List<ProtocolMessage>();
        var ch   = Substitute.For<IMessageChannel>();
        ch.IsConnected.Returns(true);
        ch.RemoteDeviceId.Returns("android");
        ch.SendAsync(
              Arg.Do<ProtocolMessage>(m => sent.Add(m)),
              Arg.Any<CancellationToken>())
          .Returns(Task.CompletedTask);

        var session = new TabletDisplaySession("sid-stop-msg", ch);
        await session.StopAsync();

        var stopMsg = sent.FirstOrDefault(m => m.Type == MessageType.MirrorStop);
        Assert.NotNull(stopMsg);

        var decoded = MirrorStopMessage.FromBytes(stopMsg!.Payload);
        Assert.Equal(0, decoded.ReasonCode); // normal stop
    }

    [Fact]
    public async Task StopAsync_CalledTwice_IsIdempotent()
    {
        var ch      = MakeChannel();
        var session = new TabletDisplaySession("sid-idem", ch);

        await session.StopAsync();
        await session.StopAsync(); // should not throw

        Assert.Equal(MirrorState.Stopped, session.State);
    }

    [Fact]
    public void StateChanged_EventFires_OnStop()
    {
        var ch     = MakeChannel();
        var states = new List<MirrorState>();
        var session = new TabletDisplaySession("sid-event", ch);
        session.StateChanged += (_, s) => states.Add(s);

        // Synchronously stop (no await; fire and forget for state-change test)
        _ = session.StopAsync();

        // Give the task a moment to run (no real async work in mock scenario)
        Thread.Sleep(50);

        Assert.Contains(MirrorState.Stopped, states);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var ch      = MakeChannel();
        var session = new TabletDisplaySession("sid-dispose", ch);
        var ex = Record.Exception(() => session.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_NullChannel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TabletDisplaySession("sid", null!));
    }

    [Fact]
    public void Constructor_NullSessionId_Throws()
    {
        var ch = MakeChannel();
        Assert.Throws<ArgumentNullException>(() =>
            new TabletDisplaySession(null!, ch));
    }
}
