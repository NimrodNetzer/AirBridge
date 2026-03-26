using AirBridge.App.Services;
using AirBridge.App.ViewModels;
using AirBridge.Core;
using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using AirBridge.Core.Pairing;
using AirBridge.Mirror;
using AirBridge.Mirror.Interfaces;
using AirBridge.Transfer;
using AirBridge.Transfer.Interfaces;
using AirBridge.Transport.Connection;
using AirBridge.Transport.Discovery;
using AirBridge.Transport.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace AirBridge.App;

/// <summary>
/// Application entry point. Configures the DI container and launches the main window.
/// </summary>
public partial class App : Application
{
    /// <summary>The application-wide service provider.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    private MainWindow? _window;

    /// <summary>Initialises the application and wires the DI container.</summary>
    public App()
    {
        InitializeComponent();
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ── Core ──────────────────────────────────────────────────────────────
        services.AddSingleton<KeyStore>();
        services.AddSingleton<PairingService>();
        // Also expose via interface (same singleton instance)
        services.AddSingleton<IPairingService>(sp => sp.GetRequiredService<PairingService>());
        services.AddSingleton<IDeviceRegistry, InMemoryDeviceRegistry>();

        // ── Transport ─────────────────────────────────────────────────────────
        // MdnsDiscoveryService needs device identity; use stable machine name + GUID
        services.AddSingleton<IDiscoveryService>(sp =>
        {
            var deviceId   = GetOrCreateDeviceId();
            var deviceName = Environment.MachineName;
            return new MdnsDiscoveryService(deviceId, deviceName, DeviceType.WindowsPc);
        });
        services.AddSingleton<IConnectionManager, TlsConnectionManager>();

        // ── Transfer ──────────────────────────────────────────────────────────
        services.AddSingleton<IFileTransferService, FileTransferServiceImpl>();

        // ── Mirror ────────────────────────────────────────────────────────────
        services.AddSingleton<IMirrorService, MirrorServiceImpl>();

        // ── App-level orchestration ───────────────────────────────────────────
        services.AddSingleton<DeviceConnectionService>();

        // ── ViewModels (transient — each navigation gets a fresh instance) ────
        services.AddTransient<DevicesViewModel>();
        services.AddTransient<PairingViewModel>();
        services.AddTransient<TransferViewModel>();
        services.AddTransient<MirrorViewModel>();
        services.AddTransient<SettingsViewModel>();

        // ── MainWindow (singleton so we can get its HWND for file pickers) ───
        services.AddSingleton<MainWindow>();
    }

    /// <inheritdoc/>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a stable device ID persisted in AppData, or creates a new GUID.
    /// </summary>
    private static string GetOrCreateDeviceId()
    {
        var dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AirBridge");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "device-id.txt");

        if (File.Exists(file))
        {
            var id = File.ReadAllText(file).Trim();
            if (!string.IsNullOrEmpty(id)) return id;
        }

        var newId = Guid.NewGuid().ToString("N");
        File.WriteAllText(file, newId);
        return newId;
    }
}
