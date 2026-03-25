using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace AirBridge.Mirror;

/// <summary>
/// Frameless, always-on-top floating window that renders the Android phone mirror stream.
/// Implements <see cref="IMirrorWindowHost"/> so it can be injected into
/// <see cref="MirrorSession"/> via the window factory parameter.
///
/// <para>
/// The window sets <c>ExtendsContentIntoTitleBar = true</c> so there is no OS chrome.
/// A <see cref="MediaPlayerElement"/> fills the entire client area and renders the
/// H.264 stream via a <see cref="MediaPlayer"/> connected to the
/// <see cref="MirrorDecoder"/>'s <see cref="Windows.Media.Core.MediaStreamSource"/>.
/// </para>
///
/// <para>
/// This class must be instantiated on the WinUI 3 dispatcher (UI) thread.
/// </para>
/// </summary>
public sealed class MirrorWindow : Window, IMirrorWindowHost
{
    private readonly IMirrorDecoder   _decoder;
    private readonly MediaPlayerElement _mediaElement;

    private AppWindow?   _appWindow;
    private MediaPlayer? _player;

    /// <summary>
    /// Creates the <see cref="MirrorWindow"/> and sets up its content.
    /// </summary>
    /// <param name="decoder">
    /// An initialized <see cref="IMirrorDecoder"/>. When the decoder is a
    /// <see cref="MirrorDecoder"/>, the window connects a <see cref="MediaPlayer"/>
    /// to the decoder's <see cref="MirrorDecoder.GetMediaStreamSource()"/> for
    /// GPU-accelerated rendering.
    /// </param>
    public MirrorWindow(IMirrorDecoder decoder)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));

        _mediaElement = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            AutoPlay                    = true,
            HorizontalAlignment         = HorizontalAlignment.Stretch,
            VerticalAlignment           = VerticalAlignment.Stretch
        };

        var grid = new Grid
        {
            Background = new SolidColorBrush(Colors.Black)
        };
        grid.Children.Add(_mediaElement);
        Content = grid;
    }

    // ── IMirrorWindowHost ──────────────────────────────────────────────────

    /// <summary>
    /// Shows the window at the given size, removes OS chrome, pins it always-on-top,
    /// and starts media playback.
    /// </summary>
    /// <param name="width">Window width in pixels.</param>
    /// <param name="height">Window height in pixels.</param>
    public void Open(int width, int height)
    {
        // Remove title bar — content fills the entire window
        ExtendsContentIntoTitleBar = true;

        // Obtain AppWindow for advanced windowing operations
        var hwnd     = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow   = AppWindow.GetFromWindowId(windowId);

        // Resize to match the stream dimensions (preserves aspect ratio by design)
        _appWindow.Resize(new SizeInt32(width, height));

        // Pin always-on-top
        NativeMethods.SetWindowTopmost(hwnd);

        // Wire up media player when using the concrete WMF-backed decoder
        var source = (_decoder as MirrorDecoder)?.GetMediaStreamSource();
        if (source is not null)
        {
            _player        = new MediaPlayer();
            _player.Source = MediaSource.CreateFromMediaStreamSource(source);
            _mediaElement.SetMediaPlayer(_player);
        }

        Activate();
    }

    /// <summary>
    /// Stops media playback and closes the window.
    /// </summary>
    public new void Close()
    {
        _player?.Pause();
        _player?.Dispose();
        _player = null;
        base.Close();
    }

    // ── P/Invoke ───────────────────────────────────────────────────────────

    private static class NativeMethods
    {
        private static readonly nint HWND_TOPMOST = new(-1);
        private const uint SWP_NOMOVE     = 0x0002;
        private const uint SWP_NOSIZE     = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>Sets the window as always-on-top via <c>SetWindowPos</c>.</summary>
        internal static void SetWindowTopmost(nint hwnd) =>
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            nint hWnd,
            nint hWndInsertAfter,
            int X, int Y, int cx, int cy,
            uint uFlags);
    }
}
