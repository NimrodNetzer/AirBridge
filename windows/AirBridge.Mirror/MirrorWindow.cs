// MirrorWindow.cs — WinUI 3 floating window for the phone mirror feature.
// Excluded from AirBridge.Mirror.csproj (net8.0 library) because it depends on
// the Windows App SDK / WinUI 3 runtime. Compiled only in AirBridge.App
// (net8.0-windows10.0.19041.0 + WindowsAppSDK). Compile guard: WINUI3.

#if WINUI3

using System.Runtime.InteropServices;
using AirBridge.Core.Interfaces;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace AirBridge.Mirror;

// ── Win32 / DWM / GDI P-Invoke ────────────────────────────────────────────
internal static class NativeMethods
{
    [DllImport("dwmapi.dll", PreserveSig = false)]
    internal static extern void DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>Creates a rounded-rectangle GDI region.</summary>
    /// <param name="cx">Width of the corner ellipse (= 2 × corner radius in px).</param>
    /// <param name="cy">Height of the corner ellipse (= 2 × corner radius in px).</param>
    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRoundRectRgn(
        int x1, int y1, int x2, int y2, int cx, int cy);

    /// <summary>
    /// Clips the window to the given region.  The OS takes ownership of the region
    /// handle — do NOT call DeleteObject on it after a successful SetWindowRgn call.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool bRedraw);

    /// <summary>Returns the DPI for the monitor that hosts the given window.</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWCP_DONOTROUND             = 1;   // no OS rounding (we handle it)
}

/// <summary>
/// WinUI 3 floating window that renders the Android phone screen mirror,
/// captures pointer/keyboard input for relay, and acts as a drag-and-drop
/// target for file transfer.
/// <para>
/// The window is frameless (<c>ExtendsContentIntoTitleBar = true</c>),
/// always-on-top, and sized to match the incoming video resolution.
/// </para>
/// <para>
/// Pointer (mouse/touch) and keyboard events are captured and surfaced via
/// <see cref="InputEventRaised"/> for forwarding to the Android device.
/// </para>
/// <para>
/// Drop target behaviour: accepts <see cref="StandardDataFormats.StorageItems"/>,
/// shows an overlay while dragging, and invokes <see cref="OnFilesDropped"/> on drop.
/// </para>
/// </summary>
public sealed class MirrorWindow : IMirrorWindowHost
{
    // ── WinUI 3 window / XAML tree ─────────────────────────────────────────

    private readonly Window              _window;
    private readonly Grid                _rootGrid;
    private readonly Border              _phoneBorder;   // outer phone-body shell
    private readonly Border              _screenBorder;  // inner screen area (clipped)
    private readonly Border              _dropOverlay;
    private readonly MediaPlayerElement  _videoElement;
    private          MediaPlayer?        _mediaPlayer;

    // Cached client dimensions for normalising pointer coordinates.
    private int _windowWidth  = 1;
    private int _windowHeight = 1;

    // Target window aspect ratio (width/height).
    // Set in Open() and used to snap the window back to the correct ratio on resize.
    private double _windowAspectRatio = 0.0;
    private bool   _suppressResize    = false;
    private IntPtr _hwnd              = IntPtr.Zero;

    // Corner radius in WinUI DIPs — must match the CornerRadius set on _screenBorder.
    private const double CornerRadiusDip = 40.0;

    // ── IMirrorWindowHost ──────────────────────────────────────────────────

    /// <summary>
    /// Raised whenever the user interacts with the mirror window via pointer or keyboard.
    /// Subscribe from <see cref="MirrorSession"/> to forward events to the Android device.
    /// </summary>
    public event EventHandler<InputEventArgs>? InputEventRaised;

    /// <summary>
    /// Callback invoked when the user drops files onto the window.
    /// Each entry is guaranteed to be a regular file (not a folder).
    /// </summary>
    public Action<IReadOnlyList<IDroppedFile>>? OnFilesDropped { get; set; }

    // ── Constructor ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the floating mirror window.
    /// </summary>
    /// <param name="decoder">
    ///   Decoder whose <see cref="IMirrorDecoder.FrameReady"/> event triggers render updates.
    ///   The window does not own the decoder.
    /// </param>
    public MirrorWindow(IMirrorDecoder decoder)
    {
        _window = new Window();
        _window.ExtendsContentIntoTitleBar = true;

        // ── Drop overlay ───────────────────────────────────────────────────
        _dropOverlay = new Border
        {
            Background          = new SolidColorBrush(
                                      Microsoft.UI.ColorHelper.FromArgb(0xBB, 0x00, 0x00, 0x00)),
            CornerRadius        = new CornerRadius(40),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Visibility          = Visibility.Collapsed,
            IsHitTestVisible    = false,
            Child               = new TextBlock
            {
                Text                = "Drop to send file",
                FontSize            = 22,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Foreground          = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };

        // ── Video element ──────────────────────────────────────────────────
        // Stretch.UniformToFill: fills the entire screen area without letterboxing.
        // This guards against any residual dimension mismatch (e.g. encoder padding)
        // that would otherwise produce gray bars along an edge.
        _videoElement = new MediaPlayerElement
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Stretch             = Stretch.UniformToFill,
        };

        // ── Screen border — the only visible container ─────────────────────
        // No bezel, no padding.  The video fills edge-to-edge and the CornerRadius
        // clips the content to the phone-screen shape.  DWM additionally clips the
        // window frame itself to the same rounded shape (Win11+).
        var screenGrid = new Grid();
        screenGrid.Children.Add(_videoElement);
        screenGrid.Children.Add(_dropOverlay);

        // No CornerRadius here — the Win32 SetWindowRgn region already rounds the
        // window outline at the OS level.  A XAML CornerRadius would clip the video
        // inside the window and expose the window background color in the corners.
        // Relying on SetWindowRgn alone gives clean edge-to-edge video with no gap.
        _screenBorder = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            Child      = screenGrid,
        };

        // _phoneBorder is retained as a field alias to _screenBorder so pointer-hit
        // normalisation (which uses _screenBorder) and the event tree below are consistent.
        _phoneBorder = _screenBorder;

        // ── Root grid ──────────────────────────────────────────────────────
        // Transparent background — the window's only visible content is the video.
        // The DWM rounded corners clip the window frame; no dark background needed.
        _rootGrid = new Grid { AllowDrop = true };
        _rootGrid.Children.Add(_screenBorder);

        // Drag-and-drop events
        _rootGrid.DragOver  += OnDragOver;
        _rootGrid.DragLeave += OnDragLeave;
        _rootGrid.Drop      += OnDrop;

        // Input relay events
        _rootGrid.PointerPressed  += OnPointerPressed;
        _rootGrid.PointerMoved    += OnPointerMoved;
        _rootGrid.PointerReleased += OnPointerReleased;
        _rootGrid.KeyDown         += OnKeyDown;
        _rootGrid.KeyUp           += OnKeyUp;

        _window.Content = _rootGrid;

        decoder.FrameReady += OnFrameReady;
    }

    // ── IMirrorWindowHost ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Show()
    {
        _window.Activate();
    }

    /// <inheritdoc/>
    public void Open(int width, int height)
    {
        _windowWidth  = width  > 0 ? width  : 1;
        _windowHeight = height > 0 ? height : 1;

        // Resize to match the phone's resolution (scaled down to fit screen if needed)
        // and pin always-on-top so it doesn't disappear behind other windows.
        var appWindow = _window.AppWindow;
        if (appWindow is not null)
        {
            // Scale down so the window is at most 80% of the screen height.
            // No bezel is added — the window dimensions are exactly the phone's pixel ratio.
            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            int screenH = displayArea.WorkArea.Height;
            int targetH = (int)(screenH * 0.80);
            double scale = (_windowHeight > targetH) ? (double)targetH / _windowHeight : 1.0;

            int winW = Math.Max(1, (int)(_windowWidth  * scale));
            int winH = Math.Max(1, (int)(_windowHeight * scale));

            appWindow.Resize(new SizeInt32(winW, winH));
            appWindow.Move(new PointInt32(100, 100));

            // Store aspect ratio and subscribe to size changes so user resizing
            // always snaps back to the phone's correct proportions.
            _windowAspectRatio = (double)winW / winH;
            appWindow.Changed += OnAppWindowChanged;

            // Always-on-top
            if (appWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = true;

            // Clip the Win32 window frame to a rounded rectangle that exactly matches
            // the CornerRadius on the XAML content, so there is no square gap between
            // the content corners and the window outline.
            // We use SetWindowRgn (GDI region) rather than DWM DWMWCP_ROUND because:
            //   - DWMWCP_ROUND only gives ~8 px system corners (too small)
            //   - SetWindowRgn respects any corner radius and the region updates on resize
            // Tell DWM not to apply its own rounding so the two don't conflict.
            try
            {
                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

                // Disable DWM's own rounding — our GDI region takes over.
                int noRound = NativeMethods.DWMWCP_DONOTROUND;
                NativeMethods.DwmSetWindowAttribute(
                    _hwnd,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref noRound,
                    sizeof(int));

                ApplyRoundedRegion(_hwnd, winW, winH);
            }
            catch
            {
                // Non-fatal: visual enhancement only.
            }
        }

        Show();
    }

    /// <summary>
    /// Attaches the decoder's <see cref="Windows.Media.Core.MediaStreamSource"/> to a
    /// <see cref="MediaPlayer"/> and begins playback, so WMF starts pulling NAL units and
    /// decoded frames appear in the window.
    /// Must be called after the decoder has been initialized with the stream resolution.
    /// </summary>
    public void AttachDecoder(IMirrorDecoder decoder)
    {
        var mss = (decoder as MirrorDecoder)?.GetMediaStreamSource();
        if (mss is null)
        {
            AirBridge.Core.AppLog.Error("[MirrorWindow] AttachDecoder: MediaStreamSource is null — decoder not initialized?");
            return;
        }

        // Subscribe to WMF pipeline events before starting playback so we capture
        // any error or premature close that would leave the window black.
        mss.Closed += (s, e) =>
            AirBridge.Core.AppLog.Error($"[MirrorWindow] MediaStreamSource.Closed — WMF shut down the pipeline");

        _mediaPlayer = new MediaPlayer { RealTimePlayback = true };
        _mediaPlayer.MediaFailed += (s, e) =>
            AirBridge.Core.AppLog.Error($"[MirrorWindow] MediaPlayer.MediaFailed — {e.ErrorMessage} (0x{e.ExtendedErrorCode:X})");
        _mediaPlayer.MediaOpened += (s, e) =>
            AirBridge.Core.AppLog.Info("[MirrorWindow] MediaPlayer.MediaOpened — pipeline ready, first frame expected soon");

        // Attach to the XAML element BEFORE setting the source so the render surface
        // is wired before WMF starts the decode pipeline.
        _videoElement.SetMediaPlayer(_mediaPlayer);
        _mediaPlayer.Source = MediaSource.CreateFromMediaStreamSource(mss);
        _mediaPlayer.Play();
    }

    /// <inheritdoc/>
    public void Close()
    {
        // WinUI 3 objects must be touched on the UI thread.
        // MirrorSession.CleanUp() may be called from a threadpool thread (StartAsync finally),
        // so always marshal through the window's DispatcherQueue.
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_mediaPlayer is not null)
            {
                // Detach from the XAML element first so WMF stops pulling frames before
                // we tear down the pipeline.  Skipping this step leaves the native COM
                // pipeline with a dangling back-pointer and causes AccessViolationException
                // when the window is subsequently closed.
                // Wrapped in try-catch: if the WMF pipeline already closed (MSS.Closed fired),
                // these calls can throw COM exceptions that would crash the app.
                try { _videoElement.SetMediaPlayer(null); } catch { }
                try { _mediaPlayer.Source = null; }         catch { }
                try { _mediaPlayer.Dispose(); }             catch { }
                _mediaPlayer = null;
            }
            try { _window.Close(); } catch { }
        });
    }

    // ── Drag-and-drop handlers ─────────────────────────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation             = DataPackageOperation.Copy;
            e.DragUIOverride.Caption        = "Send to phone";
            e.DragUIOverride.IsGlyphVisible = true;
            _dropOverlay.Visibility         = Visibility.Visible;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
        => _dropOverlay.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _dropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items
                .OfType<IStorageFile>()
                .Select(f => (IDroppedFile)new StorageFileDroppedFile(f))
                .ToList()
                .AsReadOnly();

            if (files.Count > 0)
                OnFilesDropped?.Invoke(files);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ── Input relay handlers ───────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        => RaisePointerEvent(e, InputEventType.Touch);

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Only relay mouse moves when a button is held (i.e. the user is dragging).
        // Without this check, every cursor movement over the window fires an event
        // that Android's InputInjector processes as a touch/tap — causing phantom clicks.
        var point = e.GetCurrentPoint(_screenBorder);
        if (!point.Properties.IsLeftButtonPressed &&
            !point.Properties.IsRightButtonPressed)
            return;
        RaisePointerEvent(e, InputEventType.Mouse);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        => RaisePointerEvent(e, InputEventType.Touch);

    private void RaisePointerEvent(PointerRoutedEventArgs e, InputEventType eventType)
    {
        // Normalise against the screen area (inside the phone bezel), not the full window.
        // GetCurrentPoint(_screenBorder) gives coordinates relative to the video surface origin,
        // so 0,0 is the top-left corner of the actual phone screen content.
        var point = e.GetCurrentPoint(_screenBorder);
        double w = Math.Max(1.0, _screenBorder.ActualWidth);
        double h = Math.Max(1.0, _screenBorder.ActualHeight);
        float nx = Math.Clamp((float)(point.Position.X / w), 0f, 1f);
        float ny = Math.Clamp((float)(point.Position.Y / h), 0f, 1f);
        InputEventRaised?.Invoke(this, new InputEventArgs(eventType, nx, ny));
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        InputEventRaised?.Invoke(this,
            new InputEventArgs(InputEventType.Key, 0f, 0f, Keycode: (int)e.Key));
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        InputEventRaised?.Invoke(this,
            new InputEventArgs(InputEventType.Key, 0f, 0f, Keycode: (int)e.Key));
        e.Handled = true;
    }

    // ── Decoder frame-ready ────────────────────────────────────────────────

    private void OnFrameReady(object? sender, EventArgs e)
    {
        // Frame rendering is handled by the MediaPlayer attached in AttachDecoder().
        // Nothing to do here — WMF pulls frames directly from the MediaStreamSource.
    }

    // ── Aspect-ratio enforcement ────────────────────────────────────────────

    /// <summary>
    /// Fires whenever the AppWindow size changes.  Constrains the window to the phone's
    /// aspect ratio so resizing never produces black bars or a distorted bezel.
    /// Width is treated as the authoritative dimension — height follows.
    /// Also re-applies the rounded-region clip to match the new size.
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _windowAspectRatio <= 0 || _suppressResize) return;

        var size = sender.Size;
        int expectedH = Math.Max(1, (int)Math.Round(size.Width / _windowAspectRatio));

        if (Math.Abs(expectedH - size.Height) <= 2)
        {
            // Size is already correct — just refresh the region for the new width.
            if (_hwnd != IntPtr.Zero) ApplyRoundedRegion(_hwnd, size.Width, size.Height);
            return;
        }

        _suppressResize = true;
        try
        {
            sender.Resize(new SizeInt32(size.Width, expectedH));
            if (_hwnd != IntPtr.Zero) ApplyRoundedRegion(_hwnd, size.Width, expectedH);
        }
        finally
        {
            _suppressResize = false;
        }
    }

    /// <summary>
    /// Clips the Win32 window to a rounded rectangle that exactly matches the XAML
    /// <see cref="CornerRadiusDip"/> value, so the window outline and the content
    /// corners align with no visible square gap.
    /// </summary>
    private static void ApplyRoundedRegion(IntPtr hwnd, int widthPx, int heightPx)
    {
        // Convert the XAML DIP corner radius to physical pixels using the window's DPI.
        uint dpi       = NativeMethods.GetDpiForWindow(hwnd);
        double scale   = dpi / 96.0;
        int radiusPx   = Math.Max(1, (int)Math.Round(CornerRadiusDip * scale));

        // CreateRoundRectRgn cx/cy are the *diameter* of the corner ellipse.
        var rgn = NativeMethods.CreateRoundRectRgn(
            0, 0, widthPx + 1, heightPx + 1,   // +1: GDI region is exclusive of right/bottom
            radiusPx * 2, radiusPx * 2);

        if (rgn != IntPtr.Zero)
        {
            // SetWindowRgn takes ownership — do not DeleteObject on success.
            NativeMethods.SetWindowRgn(hwnd, rgn, true);
        }
    }

    // ── StorageFileDroppedFile adapter ─────────────────────────────────────

    private sealed class StorageFileDroppedFile : IDroppedFile
    {
        private readonly IStorageFile _file;
        public StorageFileDroppedFile(IStorageFile file)
            => _file = file ?? throw new ArgumentNullException(nameof(file));
        public string Name => _file.Name;
        public string Path => _file.Path;
    }
}

#endif // WINUI3
