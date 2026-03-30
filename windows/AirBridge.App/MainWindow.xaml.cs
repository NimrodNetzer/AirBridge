using AirBridge.App.Pages;
using AirBridge.App.Services;
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

        // Auto-start discovery so the TCP listener is always ready when Android connects.
        _ = AutoStartDiscoveryAsync();

        // Subscribe here (MainWindow always has a valid XamlRoot) so the pairing dialog
        // pops up regardless of which page is currently visible.
        var connectionSvc = App.Services.GetService(typeof(DeviceConnectionService)) as DeviceConnectionService;
        if (connectionSvc is not null)
            connectionSvc.IncomingPairingRequest += OnIncomingPairingRequest;

        // Eagerly resolve MirrorViewModel on the UI thread so its DispatcherQueue is captured
        // correctly and it subscribes to AndroidMirrorStartRequested before Android can initiate
        // a mirror session (even if the user never navigates to the Mirror page).
        _ = App.Services.GetService(typeof(AirBridge.App.ViewModels.MirrorViewModel));
    }

    private bool _pairingDialogOpen;

    private void OnIncomingPairingRequest(object? sender, (string Pin, string DeviceId) info)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_pairingDialogOpen) return;
            _pairingDialogOpen = true;
            try
            {
                var dialog = new AirBridge.App.Pages.PairingDialog(null)
                {
                    XamlRoot = Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch { /* ignore UI errors */ }
            finally
            {
                _pairingDialogOpen = false;
            }
        });
    }

    private async Task AutoStartDiscoveryAsync()
    {
        try
        {
            var svc = App.Services.GetService(typeof(DeviceConnectionService)) as DeviceConnectionService;
            if (svc is not null)
                await svc.StartDiscoveryAsync().ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal: user can still start manually via the Devices page
        }
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
