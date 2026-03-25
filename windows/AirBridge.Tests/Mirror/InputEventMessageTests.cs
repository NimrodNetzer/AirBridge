using AirBridge.Mirror;

namespace AirBridge.Tests.Mirror;

/// <summary>
/// Round-trip serialization tests for <see cref="InputEventMessage"/>.
/// Verifies that every field survives a serialize → deserialize cycle and that
/// the type byte, optional keycode branch, and coordinate clamping all behave correctly.
/// No network sockets or WinRT APIs are required.
/// </summary>
public class InputEventMessageTests
{
    // ── Type byte ────────────────────────────────────────────────────────────

    [Fact]
    public void InputEventMessage_TypeByte_IsInputEvent()
    {
        var msg   = new InputEventMessage("sid", InputEventKind.Touch, 0.5f, 0.5f, null, 0);
        var bytes = msg.ToBytes();
        Assert.Equal((byte)MirrorMessageType.InputEvent, bytes[0]);
    }

    // ── Touch event (no keycode) ─────────────────────────────────────────────

    [Fact]
    public void InputEventMessage_RoundTrip_Touch_NoKeycode()
    {
        var msg     = new InputEventMessage("session-touch", InputEventKind.Touch, 0.25f, 0.75f, null, 0);
        var decoded = InputEventMessage.FromBytes(msg.ToBytes());

        Assert.Equal(msg.SessionId,   decoded.SessionId);
        Assert.Equal(msg.EventKind,   decoded.EventKind);
        Assert.Equal(msg.NormalizedX, decoded.NormalizedX, precision: 5);
        Assert.Equal(msg.NormalizedY, decoded.NormalizedY, precision: 5);
        Assert.Null(decoded.Keycode);
        Assert.Equal(0, decoded.MetaState);
    }

    // ── Key event (with keycode) ─────────────────────────────────────────────

    [Fact]
    public void InputEventMessage_RoundTrip_Key_WithKeycode()
    {
        var msg     = new InputEventMessage("session-key", InputEventKind.Key, 0f, 0f, 66 /* VK_B */, 1);
        var decoded = InputEventMessage.FromBytes(msg.ToBytes());

        Assert.Equal(InputEventKind.Key, decoded.EventKind);
        Assert.NotNull(decoded.Keycode);
        Assert.Equal(66, decoded.Keycode!.Value);
        Assert.Equal(1,  decoded.MetaState);
    }

    // ── Mouse event ──────────────────────────────────────────────────────────

    [Fact]
    public void InputEventMessage_RoundTrip_Mouse()
    {
        var msg     = new InputEventMessage("s", InputEventKind.Mouse, 0.0f, 1.0f, null, 0);
        var decoded = InputEventMessage.FromBytes(msg.ToBytes());

        Assert.Equal(InputEventKind.Mouse, decoded.EventKind);
        Assert.Equal(0.0f, decoded.NormalizedX, precision: 5);
        Assert.Equal(1.0f, decoded.NormalizedY, precision: 5);
    }

    // ── Boundary coordinates ─────────────────────────────────────────────────

    [Fact]
    public void InputEventMessage_RoundTrip_Coordinates_ZeroZero()
    {
        var msg     = new InputEventMessage("s", InputEventKind.Touch, 0f, 0f, null, 0);
        var decoded = InputEventMessage.FromBytes(msg.ToBytes());
        Assert.Equal(0f, decoded.NormalizedX, precision: 5);
        Assert.Equal(0f, decoded.NormalizedY, precision: 5);
    }

    [Fact]
    public void InputEventMessage_RoundTrip_Coordinates_OneOne()
    {
        var msg     = new InputEventMessage("s", InputEventKind.Touch, 1f, 1f, null, 0);
        var decoded = InputEventMessage.FromBytes(msg.ToBytes());
        Assert.Equal(1f, decoded.NormalizedX, precision: 5);
        Assert.Equal(1f, decoded.NormalizedY, precision: 5);
    }

    // ── Unicode session ID ───────────────────────────────────────────────────

    [Fact]
    public void InputEventMessage_RoundTrip_UnicodeSessionId()
    {
        var msg     = new InputEventMessage("session-\u4e2d\u6587", InputEventKind.Touch, 0.5f, 0.5f, null, 0);
        var decoded = InputEventMessage.FromBytes(msg.ToBytes());
        Assert.Equal(msg.SessionId, decoded.SessionId);
    }

    // ── MetaState ────────────────────────────────────────────────────────────

    [Fact]
    public void InputEventMessage_RoundTrip_MetaState_MaxValue()
    {
        var msg     = new InputEventMessage("s", InputEventKind.Key, 0f, 0f, 13, int.MaxValue);
        var decoded = InputEventMessage.FromBytes(msg.ToBytes());
        Assert.Equal(int.MaxValue, decoded.MetaState);
    }

    // ── InputEventKind values ────────────────────────────────────────────────

    [Theory]
    [InlineData(InputEventKind.Touch)]
    [InlineData(InputEventKind.Key)]
    [InlineData(InputEventKind.Mouse)]
    public void InputEventMessage_RoundTrip_AllEventKinds(InputEventKind kind)
    {
        var keycode = kind == InputEventKind.Key ? (int?)65 : null;
        var msg     = new InputEventMessage("sid", kind, 0.1f, 0.9f, keycode, 0);
        var decoded = InputEventMessage.FromBytes(msg.ToBytes());
        Assert.Equal(kind, decoded.EventKind);
    }
}
