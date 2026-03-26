using AirBridge.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace AirBridge.App.Pages;

/// <summary>
/// Page for starting and stopping phone-window and tablet-display mirror sessions.
/// </summary>
public sealed partial class MirrorPage : Page
{
    /// <summary>The ViewModel backing this page.</summary>
    public MirrorViewModel ViewModel { get; }

    /// <summary>Initialises the page and resolves its ViewModel from DI.</summary>
    public MirrorPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetService(typeof(MirrorViewModel)) as MirrorViewModel
                    ?? throw new InvalidOperationException("MirrorViewModel not registered.");
    }
}
