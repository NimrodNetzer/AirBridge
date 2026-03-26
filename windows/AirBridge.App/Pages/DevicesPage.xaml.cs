using AirBridge.App.ViewModels;
using AirBridge.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AirBridge.App.Pages;

/// <summary>
/// Displays discovered devices on the local network and lets the user initiate
/// connection and pairing.
/// </summary>
public sealed partial class DevicesPage : Page
{
    /// <summary>The ViewModel backing this page.</summary>
    public DevicesViewModel ViewModel { get; }

    /// <summary>Initialises the page and resolves its ViewModel from DI.</summary>
    public DevicesPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetService(typeof(DevicesViewModel)) as DevicesViewModel
                    ?? throw new InvalidOperationException("DevicesViewModel not registered.");
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DeviceInfo device })
            await ShowPairingDialogAsync(device);
    }

    private async Task ShowPairingDialogAsync(DeviceInfo device)
    {
        var dialog = new PairingDialog(device)
        {
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }
}
