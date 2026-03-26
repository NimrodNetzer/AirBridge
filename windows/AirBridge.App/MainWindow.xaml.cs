using AirBridge.App.Pages;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace AirBridge.App;

/// <summary>
/// Main application window. Hosts a <see cref="NavigationView"/> in left-compact
/// mode and a <see cref="Frame"/> that navigates between the app's pages.
/// Applies a Mica system backdrop for the modern Fluent translucency effect.
/// </summary>
public sealed partial class MainWindow : Window
{
    private MicaController? _mica;
    private SystemBackdropConfiguration? _backdropConfig;

    /// <summary>Initialises the window, applies Mica, and navigates to the Devices page.</summary>
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        Title = "AirBridge";

        TrySetMicaBackdrop();

        // Navigate to Devices page on startup
        ContentFrame.Navigate(typeof(DevicesPage));
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        Type? pageType = tag switch
        {
            "devices"  => typeof(DevicesPage),
            "transfer" => typeof(TransferPage),
            "mirror"   => typeof(MirrorPage),
            _          => null
        };

        if (pageType is not null)
            ContentFrame.Navigate(pageType);
    }

    // ── Mica backdrop ─────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to apply the Mica system backdrop. Falls down gracefully on
    /// systems that do not support it (e.g. Windows 10 without the right updates).
    /// </summary>
    private void TrySetMicaBackdrop()
    {
        if (!MicaController.IsSupported()) return;

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true
        };

        _mica = new MicaController();

        // Wire activation state so Mica dims correctly when the window loses focus
        Activated   += (_, args) =>
            _backdropConfig.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        Closed      += (_, _) => { _mica?.Dispose(); _mica = null; };

        _mica.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _mica.SetSystemBackdropConfiguration(_backdropConfig);
    }
}
