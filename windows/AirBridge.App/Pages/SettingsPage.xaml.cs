using AirBridge.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirBridge.App.Pages;

/// <summary>
/// Settings page: auto-start toggle, paired device management, and app info.
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>The ViewModel backing this page.</summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>Initialises the page and resolves its ViewModel from DI.</summary>
    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetService(typeof(SettingsViewModel)) as SettingsViewModel
                    ?? throw new InvalidOperationException("SettingsViewModel not registered.");
    }

    private async void RevokeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string deviceId })
            await ViewModel.RevokeCommand.ExecuteAsync(deviceId);
    }
}
