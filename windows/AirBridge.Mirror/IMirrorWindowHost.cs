using AirBridge.Core.Interfaces;

namespace AirBridge.Mirror;

/// <summary>
/// Abstracts the floating mirror window shown on the Windows desktop.
/// Implemented by the WinUI 3 <c>MirrorWindow</c> class in the App layer;
/// mocked in unit tests.
/// </summary>
public interface IMirrorWindowHost
{
    /// <summary>
    /// Raised whenever the user interacts with the mirror window via pointer or keyboard.
    /// Subscribe from <see cref="MirrorSession"/> to forward events to the Android device.
    /// </summary>
    event EventHandler<InputEventArgs>? InputEventRaised;
    /// <summary>
    /// Shows the window and begins presenting decoded frames.
    /// Must be called on the UI thread.
    /// </summary>
    void Show();

    /// <summary>Hides and closes the window.</summary>
    void Close();

    /// <summary>
    /// Callback invoked whenever the user drops one or more files onto the window.
    /// The session assigns this to route dropped files through
    /// <see cref="MirrorSession.SendFileAsync"/>.
    /// </summary>
    Action<IReadOnlyList<IDroppedFile>>? OnFilesDropped { get; set; }
}
