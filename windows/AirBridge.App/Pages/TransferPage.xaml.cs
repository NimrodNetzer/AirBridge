using AirBridge.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AirBridge.App.Pages;

/// <summary>
/// Page for sending and tracking file transfers to connected devices.
/// </summary>
public sealed partial class TransferPage : Page
{
    /// <summary>The ViewModel backing this page.</summary>
    public TransferViewModel ViewModel { get; }

    /// <summary>Initialises the page and resolves its ViewModel from DI.</summary>
    public TransferPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetService(typeof(TransferViewModel)) as TransferViewModel
                    ?? throw new InvalidOperationException("TransferViewModel not registered.");
    }

    private async void SendFileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");

        // Associate picker with the main window HWND (required on Win32)
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
            App.Services.GetService(typeof(MainWindow)) as MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await ViewModel.SendFileCommand.ExecuteAsync(file.Path);
    }

    private void TransferErrorBar_CloseButtonClick(InfoBar sender, object args)
        => ViewModel.DismissErrorCommand.Execute(null);
}
