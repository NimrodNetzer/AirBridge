namespace AirBridge.Mirror;

/// <summary>
/// Abstraction over a file that was dropped onto the mirror window.
/// Decouples <see cref="MirrorSession"/> from the WinUI 3
/// <c>Windows.Storage.IStorageFile</c> type so that unit tests
/// can provide in-memory implementations without depending on
/// the Windows App SDK.
/// </summary>
public interface IDroppedFile
{
    /// <summary>
    /// Display name of the file (e.g. <c>"photo.jpg"</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Absolute path to the file on the local filesystem.
    /// </summary>
    string Path { get; }
}
