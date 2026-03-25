namespace AirBridge.Core.Interfaces;

/// <summary>Mode of the mirror session.</summary>
public enum MirrorMode { PhoneWindow, TabletDisplay }

/// <summary>State of a mirror session.</summary>
public enum MirrorState { Connecting, Active, Paused, Stopped, Error }

/// <summary>
/// Represents an active screen mirror session between a Windows PC and an Android device.
/// The Windows side is always the consumer (renders frames or drives a virtual display).
/// </summary>
public interface IMirrorSession : IDisposable
{
    string SessionId { get; }
    MirrorMode Mode { get; }
    MirrorState State { get; }

    /// <summary>Starts the session. Resolves once the first frame is received.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the session gracefully.</summary>
    Task StopAsync();

    /// <summary>Sends an input event to the remote Android device.</summary>
    Task SendInputAsync(InputEventArgs inputEvent, CancellationToken cancellationToken = default);

    event EventHandler<MirrorState> StateChanged;
}

/// <summary>Describes an input event to relay to the Android device.</summary>
public sealed record InputEventArgs(
    InputEventType Type,
    float NormalizedX,
    float NormalizedY,
    int? Keycode = null,
    int MetaState = 0
);

/// <summary>Type of input event.</summary>
public enum InputEventType { Touch, Key, Mouse }
