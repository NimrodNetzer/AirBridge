// MirrorWindow.cs — WinUI 3 floating window for the phone mirror feature.
// Excluded from AirBridge.Mirror.csproj (net8.0 library) because it depends on
// the Windows App SDK / WinUI 3 runtime. Compiled only in AirBridge.App
// (net8.0-windows10.0.19041.0 + WindowsAppSDK). Compile guard: WINUI3.

#if WINUI3

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
    private readonly Border              _dropOverlay;
    private readonly MediaPlayerElement  _videoElement;
    private          MediaPlayer?        _mediaPlayer;

    // Cached client dimensions for normalising pointer coordinates.
    private int _windowWidth  = 1;
    private int _windowHeight = 1;

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
                                      Microsoft.UI.ColorHelper.FromArgb(0xCC, 0x00, 0x00, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Visibility          = Visibility.Collapsed,
            IsHitTestVisible    = false,
            Child               = new TextBlock
            {
                Text       = "Drop to send file",
                FontSize   = 24,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };

        // ── Video element ──────────────────────────────────────────────────
        _videoElement = new MediaPlayerElement
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Stretch             = Stretch.Uniform,
        };

        // ── Root grid ──────────────────────────────────────────────────────
        _rootGrid = new Grid { AllowDrop = true };
        _rootGrid.Children.Add(_videoElement);
        _rootGrid.Children.Add(_dropOverlay);

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
            // Scale down if the phone resolution is taller than 80% of the screen height.
            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            int screenH = displayArea.WorkArea.Height;
            int targetH = (int)(screenH * 0.8);
            double scale = (_windowHeight > targetH) ? (double)targetH / _windowHeight : 1.0;

            int winW = Math.Max(1, (int)(_windowWidth  * scale));
            int winH = Math.Max(1, (int)(_windowHeight * scale));

            appWindow.Resize(new SizeInt32(winW, winH));
            appWindow.Move(new PointInt32(100, 100));

            // Always-on-top
            if (appWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = true;
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
        if (mss is null) return;

        _mediaPlayer = new MediaPlayer { RealTimePlayback = true, AutoPlay = true };
        _mediaPlayer.Source = MediaSource.CreateFromMediaStreamSource(mss);
        _videoElement.SetMediaPlayer(_mediaPlayer);
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
                _videoElement.SetMediaPlayer(null);
                _mediaPlayer.Source = null;
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }
            _window.Close();
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
        => RaisePointerEvent(e, InputEventType.Mouse);

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        => RaisePointerEvent(e, InputEventType.Touch);

    private void RaisePointerEvent(PointerRoutedEventArgs e, InputEventType eventType)
    {
        var point = e.GetCurrentPoint(_rootGrid);
        // Normalize against the grid's actual rendered size (DIPs), not the phone's native
        // pixel resolution.  _windowWidth/_windowHeight are the phone's pixel dimensions
        // (e.g. 1080×2340) but GetCurrentPoint returns coordinates in WinUI DIPs (e.g. 0-400),
        // so dividing by the phone resolution produces a tiny fraction and maps every click
        // to the top-left corner of the screen.
        double w = Math.Max(1.0, _rootGrid.ActualWidth);
        double h = Math.Max(1.0, _rootGrid.ActualHeight);
        float nx  = Math.Clamp((float)(point.Position.X / w), 0f, 1f);
        float ny  = Math.Clamp((float)(point.Position.Y / h), 0f, 1f);
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
