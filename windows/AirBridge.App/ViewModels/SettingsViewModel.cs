using AirBridge.Core.Interfaces;
using AirBridge.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;

namespace AirBridge.App.ViewModels;

/// <summary>
/// ViewModel for the Settings page.
/// Manages auto-start registry entry and the list of paired devices.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const string StartupKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "AirBridge";

    private readonly IPairingService _pairing;
    private readonly IDeviceRegistry _registry;

    /// <summary>Collection of paired devices shown in the UI.</summary>
    public ObservableCollection<DeviceInfo> PairedDevices { get; } = new();

    /// <summary>True when no devices are paired (drives the "no devices" hint visibility).</summary>
    public bool HasNoPairedDevices => PairedDevices.Count == 0;

    [ObservableProperty]
    private bool _autoStart;

    /// <summary>Application version string.</summary>
    public string Version => "1.0.0";

    /// <summary>
    /// Initialises the SettingsViewModel and loads current state.
    /// </summary>
    public SettingsViewModel(IPairingService pairing, IDeviceRegistry registry)
    {
        _pairing  = pairing;
        _registry = registry;

        LoadAutoStart();
        LoadPairedDevices();

        // Refresh paired list whenever a device changes
        _registry.DeviceChanged += (_, _) => LoadPairedDevices();
    }

    private void LoadAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath);
        AutoStart = key?.GetValue(StartupValueName) is not null;
    }

    private void LoadPairedDevices()
    {
        var paired = _registry.GetPairedDevices();
        PairedDevices.Clear();
        foreach (var device in paired)
            PairedDevices.Add(device);
        OnPropertyChanged(nameof(HasNoPairedDevices));
    }

    /// <summary>Toggles the Windows startup registry entry for AirBridge.</summary>
    [RelayCommand]
    private void ToggleAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, writable: true);
        if (key is null) return;

        if (AutoStart)
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            key.SetValue(StartupValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Revokes pairing for the device with the given ID.</summary>
    [RelayCommand]
    private async Task RevokeAsync(string deviceId)
    {
        await _pairing.RevokePairingAsync(deviceId);
        _registry.Remove(deviceId);
        LoadPairedDevices();
    }
}
