using AirBridge.Mirror;
using AirBridge.Transport.Interfaces;
using AirBridge.Transport.Protocol;
using NSubstitute;

namespace AirBridge.Tests;

/// <summary>
/// Unit tests for the drag-and-drop file transfer feature on
/// <see cref="MirrorSession"/> and related components.
/// <para>
/// All tests run without network sockets or WinUI 3 runtime;
/// every external dependency is mocked via NSubstitute.
/// </para>
/// </summary>
public class MirrorDragDropTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal <see cref="IMessageChannel"/> stub that:
    /// <list type="bullet">
    ///   <item>Reports as connected.</item>
    ///   <item>Accepts <c>SendAsync</c> calls without error.</item>
    ///   <item>Returns <c>null</c> on the first <c>ReceiveAsync</c>
    ///         (simulates a cleanly closed channel).</item>
    /// </list>
    /// </summary>
    private static IMessageChannel MakeClosedChannel()
    {
        var ch = Substitute.For<IMessageChannel>();
        ch.IsConnected.Returns(true);
        ch.RemoteDeviceId.Returns("test-device");
        ch.ReceiveAsync(Arg.Any<CancellationToken>())
          .Returns(Task.FromResult<ProtocolMessage?>(null));
        return ch;
    }

    /// <summary>
    /// Creates a minimal <see cref="IDroppedFile"/> stub for
    /// <paramref name="filePath"/>.
    /// </summary>
    private static IDroppedFile MakeFile(string filePath)
    {
        var f = Substitute.For<IDroppedFile>();
        f.Path.Returns(filePath);
        f.Name.Returns(Path.GetFileName(filePath));
        return f;
    }

    // ── IMirrorWindowHost callback ─────────────────────────────────────────

    /// <summary>
    /// When a window host fires <see cref="IMirrorWindowHost.OnFilesDropped"/>,
    /// <see cref="MirrorSession"/> must invoke <see cref="ITransferEngine.SendFileAsync"/>
    /// once per dropped file.
    /// </summary>
    [Fact]
    public async Task OnFilesDropped_CallsSendFileAsyncForEachFile()
    {
        // Arrange
        var engine = Substitute.For<ITransferEngine>();
        engine.SendFileAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<IProgress<long>?>(), Arg.Any<CancellationToken>())
              .Returns(TransferEngineResult.Success);

        // Capture the OnFilesDropped callback assigned by MirrorSession.
        Action<IReadOnlyList<IDroppedFile>>? capturedCallback = null;

        var windowHost = Substitute.For<IMirrorWindowHost>();
        // When MirrorSession sets OnFilesDropped, store the value in capturedCallback.
        windowHost.When(w => w.OnFilesDropped = Arg.Any<Action<IReadOnlyList<IDroppedFile>>?>())
                  .Do(ci => capturedCallback = ci.Arg<Action<IReadOnlyList<IDroppedFile>>?>());

        var channel = MakeClosedChannel();
        // Return MIRROR_START then null so the session loops once and exits.
        channel.ReceiveAsync(Arg.Any<CancellationToken>())
               .Returns(
                   Task.FromResult<ProtocolMessage?>(
                       new ProtocolMessage(MessageType.MirrorStart, Array.Empty<byte>())),
                   Task.FromResult<ProtocolMessage?>(null));

        var decoder = Substitute.For<IMirrorDecoder>();
        var session = new MirrorSession(
            sessionId:      Guid.NewGuid().ToString(),
            channel:        channel,
            decoderFactory: () => decoder,
            windowFactory:  _ => windowHost,
            transferEngine: engine);

        // Act — run the session to completion so the callback is assigned
        await session.StartAsync(CancellationToken.None);

        Assert.NotNull(capturedCallback);

        var files = new List<IDroppedFile>
        {
            MakeFile(@"C:\Users\test\photo.jpg"),
            MakeFile(@"C:\Users\test\document.pdf"),
        };

        // Invoke the callback (simulates user dropping files onto the window)
        capturedCallback!(files.AsReadOnly());

        // Allow the async fire-and-forget to complete
        await Task.Delay(200);

        // Assert — engine called once per file
        await engine.Received(1).SendFileAsync(
            Arg.Is<string>(p => p.EndsWith("photo.jpg")),
            Arg.Any<Stream>(),
            Arg.Any<IProgress<long>?>(),
            Arg.Any<CancellationToken>());

        await engine.Received(1).SendFileAsync(
            Arg.Is<string>(p => p.EndsWith("document.pdf")),
            Arg.Any<Stream>(),
            Arg.Any<IProgress<long>?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When no <see cref="ITransferEngine"/> is injected,
    /// invoking <see cref="MirrorSession.SendFileAsync"/> directly
    /// must complete without throwing.
    /// </summary>
    [Fact]
    public async Task SendFileAsync_NullEngine_IsNoOp()
    {
        // Arrange
        var channel = MakeClosedChannel();
        var session = new MirrorSession(
            sessionId:      Guid.NewGuid().ToString(),
            channel:        channel,
            decoderFactory: null,
            windowFactory:  null,
            transferEngine: null);  // <-- no engine

        var file = MakeFile(@"C:\Users\test\video.mp4");

        // Act & Assert — must not throw
        var exception = await Record.ExceptionAsync(
            () => session.SendFileAsync(file, CancellationToken.None));

        Assert.Null(exception);
    }

    /// <summary>
    /// <see cref="MirrorSession.SendFileAsync"/> must call
    /// <see cref="ITransferEngine.SendFileAsync"/> with the exact file path
    /// returned by <see cref="IDroppedFile.Path"/>.
    /// </summary>
    [Fact]
    public async Task SendFileAsync_PassesCorrectPathToEngine()
    {
        // Arrange
        const string expectedPath = @"C:\Shared\report.xlsx";

        var channel = MakeClosedChannel();
        var engine  = Substitute.For<ITransferEngine>();
        engine.SendFileAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<IProgress<long>?>(), Arg.Any<CancellationToken>())
              .Returns(TransferEngineResult.Success);

        var session = new MirrorSession(
            sessionId:      Guid.NewGuid().ToString(),
            channel:        channel,
            transferEngine: engine);

        var file = MakeFile(expectedPath);

        // Act
        await session.SendFileAsync(file, CancellationToken.None);

        // Assert
        await engine.Received(1).SendFileAsync(
            expectedPath,
            Arg.Any<Stream>(),
            Arg.Any<IProgress<long>?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When the engine returns a failure result,
    /// <see cref="MirrorSession.SendFileAsync"/> must not throw —
    /// errors are informational and the mirror session continues.
    /// </summary>
    [Fact]
    public async Task SendFileAsync_EngineFailure_DoesNotThrow()
    {
        // Arrange
        var channel = MakeClosedChannel();
        var engine  = Substitute.For<ITransferEngine>();
        engine.SendFileAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<IProgress<long>?>(), Arg.Any<CancellationToken>())
              .Returns(TransferEngineResult.Failure("Disk full"));

        var session = new MirrorSession(
            sessionId:      Guid.NewGuid().ToString(),
            channel:        channel,
            transferEngine: engine);

        var file = MakeFile(@"C:\temp\big_file.zip");

        // Act & Assert — must not throw
        var exception = await Record.ExceptionAsync(
            () => session.SendFileAsync(file, CancellationToken.None));

        Assert.Null(exception);
    }

    // ── MirrorSession constructor / state ──────────────────────────────────

    /// <summary>
    /// A freshly created session must have the <c>Connecting</c> state.
    /// </summary>
    [Fact]
    public void NewSession_StateIsConnecting()
    {
        var channel = MakeClosedChannel();
        var session = new MirrorSession(Guid.NewGuid().ToString(), channel);

        Assert.Equal(AirBridge.Core.Interfaces.MirrorState.Connecting, session.State);
    }

    /// <summary>
    /// <see cref="MirrorSession.SessionId"/> must be the value passed to the constructor.
    /// </summary>
    [Fact]
    public void SessionId_MatchesConstructorArgument()
    {
        var id      = Guid.NewGuid().ToString();
        var channel = MakeClosedChannel();
        var session = new MirrorSession(id, channel);

        Assert.Equal(id, session.SessionId);
    }

    // ── IDroppedFile ───────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a mock <see cref="IDroppedFile"/> correctly exposes
    /// <see cref="IDroppedFile.Name"/> and <see cref="IDroppedFile.Path"/>.
    /// </summary>
    [Fact]
    public void DroppedFile_NameAndPath_AreAccessible()
    {
        const string path = @"C:\Downloads\archive.zip";
        var file = MakeFile(path);

        Assert.Equal(path, file.Path);
        Assert.Equal("archive.zip", file.Name);
    }

    // ── TransferEngineResult ───────────────────────────────────────────────

    /// <summary>
    /// <see cref="TransferEngineResult.Success"/> is a singleton success value.
    /// </summary>
    [Fact]
    public void TransferEngineResult_Success_IsSuccess()
    {
        Assert.True(TransferEngineResult.Success.IsSuccess);
        Assert.Null(TransferEngineResult.Success.ErrorMessage);
    }

    /// <summary>
    /// <see cref="TransferEngineResult.Failure(string)"/> carries the error message.
    /// </summary>
    [Fact]
    public void TransferEngineResult_Failure_HasErrorMessage()
    {
        var result = TransferEngineResult.Failure("Connection reset");

        Assert.False(result.IsSuccess);
        Assert.Equal("Connection reset", result.ErrorMessage);
    }
}
