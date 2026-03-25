// MirrorWindow.cs — WinUI 3 floating window for the phone mirror feature.
// This file is EXCLUDED from AirBridge.Mirror.csproj (net8.0 library) because
// it depends on the Windows App SDK / WinUI 3 runtime.
// It is compiled only when included directly in the AirBridge.App project
// (net8.0-windows10.0.19041.0 + WindowsAppSDK).
//
// Compile guard: WINUI3 — defined by AirBridge.App.csproj.

#if WINUI3

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace AirBridge.Mirror;

/// <summary>
/// WinUI 3 floating window that renders the Android phone screen mirror
/// and acts as a drag-and-drop target for file transfer.
/// <para>
/// The window is frameless (<c>ExtendsContentIntoTitleBar = true</c>),
/// always-on-top, and sized to match the incoming video resolution.
/// </para>
/// <para>
/// Drop target behaviour:
/// <list type="bullet">
///   <item>Accepts only <see cref="StandardDataFormats.StorageItems"/>.</item>
///   <item>Shows a semi-transparent overlay while a drag is in progress.</item>
///   <item>On drop, invokes <see cref="OnFilesDropped"/> with the list of
///         dropped <see cref="IDroppedFile"/> instances.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MirrorWindow : IMirrorWindowHost
{
    // ── WinUI 3 window / XAML tree ─────────────────────────────────────────

    private readonly Window _window;
    private readonly Grid   _rootGrid;
    private readonly TextBlock _dropOverlay;

    // ── IMirrorWindowHost ──────────────────────────────────────────────────

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
    ///   Decoder whose <see cref="IMirrorDecoder.FrameReady"/> event is used
    ///   to trigger render updates.  The window does not own the decoder.
    /// </param>
    public MirrorWindow(IMirrorDecoder decoder)
    {
        _window = new Window();
        _window.ExtendsContentIntoTitleBar = true;

        // ── Drop overlay TextBlock ─────────────────────────────────────────
        _dropOverlay = new TextBlock
        {
            Text                = "Drop to send file",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            FontSize            = 24,
            Foreground          = new SolidColorBrush(Microsoft.UI.Colors.White),
            Background          = new SolidColorBrush(
                                      Microsoft.UI.ColorHelper.FromArgb(0xCC, 0x00, 0x00, 0x00)),
            Visibility          = Visibility.Collapsed,
            IsHitTestVisible    = false,   // pass pointer events through to the grid
        };

        // ── Root grid (drop target) ────────────────────────────────────────
        _rootGrid = new Grid
        {
            AllowDrop = true,
        };
        _rootGrid.Children.Add(_dropOverlay);

        _rootGrid.DragOver  += OnDragOver;
        _rootGrid.DragLeave += OnDragLeave;
        _rootGrid.Drop      += OnDrop;

        _window.Content = _rootGrid;

        // Hook decoder frame-ready if needed for future render wiring
        decoder.FrameReady += OnFrameReady;
    }

    // ── IMirrorWindowHost ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Show() => _window.Activate();

    /// <inheritdoc/>
    public void Close() => _window.Close();

    // ── Drag-and-drop handlers ─────────────────────────────────────────────

    /// <summary>
    /// Handles the <c>DragOver</c> event.
    /// Accepts the drag operation if storage items are present and shows the overlay.
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption        = "Send to phone";
            e.DragUIOverride.IsGlyphVisible = true;
            _dropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    /// <summary>
    /// Handles the <c>DragLeave</c> event; collapses the overlay.
    /// </summary>
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dropOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Handles the <c>Drop</c> event.
    /// Extracts <see cref="IStorageFile"/> items, wraps them in
    /// <see cref="StorageFileDroppedFile"/>, and invokes
    /// <see cref="OnFilesDropped"/>.
    /// </summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        _dropOverlay.Visibility = Visibility.Collapsed;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        // GetDeferral keeps the DataPackage valid across the async await.
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

    /// <summary>
    /// Called when the decoder has a new frame ready for display.
    /// Frame rendering (Direct3D surface update) is wired in Iteration 6.
    /// </summary>
    private void OnFrameReady(object? sender, EventArgs e)
    {
        // TODO (Iteration 6): blit decoded frame to the window surface.
    }

    // ── StorageFileDroppedFile adapter ─────────────────────────────────────

    /// <summary>
    /// Adapts a WinRT <see cref="IStorageFile"/> to the platform-agnostic
    /// <see cref="IDroppedFile"/> interface used by <see cref="MirrorSession"/>.
    /// </summary>
    private sealed class StorageFileDroppedFile : IDroppedFile
    {
        private readonly IStorageFile _file;

        /// <summary>Wraps <paramref name="file"/>.</summary>
        public StorageFileDroppedFile(IStorageFile file) =>
            _file = file ?? throw new ArgumentNullException(nameof(file));

        /// <inheritdoc/>
        public string Name => _file.Name;

        /// <inheritdoc/>
        public string Path => _file.Path;
    }
}

#endif // WINUI3
