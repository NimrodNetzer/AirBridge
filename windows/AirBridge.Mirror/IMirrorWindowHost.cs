namespace AirBridge.Mirror;

/// <summary>
/// Abstraction over a window that renders the mirror stream.
/// Implemented by <c>MirrorWindow</c> in the WinUI 3 app project.
/// A no-op stub is used in unit tests so the state-machine logic
/// can be exercised without any UI infrastructure.
/// </summary>
public interface IMirrorWindowHost
{
    /// <summary>
    /// Opens (or shows) the window at the given dimensions and begins rendering.
    /// </summary>
    /// <param name="width">Stream width in pixels.</param>
    /// <param name="height">Stream height in pixels.</param>
    void Open(int width, int height);

    /// <summary>
    /// Closes the window and releases rendering resources.
    /// </summary>
    void Close();
}
