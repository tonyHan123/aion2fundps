using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Aion2FunDps.App;

/// <summary>
/// Modal dialog shown at startup when <see cref="NpcapDetector.IsInstalled"/>
/// returns false. Offers a one-click button to open Npcap's official
/// download page in the user's default browser; the user installs through
/// Npcap's own UI installer (license-clean — we don't redistribute,
/// silently install, or otherwise touch the binary).
/// </summary>
public partial class NpcapMissingWindow : Window
{
    private const string NpcapDownloadUrl = "https://npcap.com/#download";

    public NpcapMissingWindow()
    {
        InitializeComponent();
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute=true so the URL opens in the user's default
            // browser instead of failing on a Process.Start that expects an
            // exe path. .NET 6+ defaults UseShellExecute to false, so we
            // must set it explicitly for URLs.
            Process.Start(new ProcessStartInfo
            {
                FileName = NpcapDownloadUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Browser launch failure is non-fatal — fall through to keeping
            // the dialog open so the user can copy the URL from a help text
            // / try the button again.
        }

        // Close the dialog after opening the site. The app will exit
        // (handled by App.OnStartup which checks return value); user
        // restarts the meter after Npcap install.
        DialogResult = false;
        Close();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
