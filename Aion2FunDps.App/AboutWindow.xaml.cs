using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace Aion2FunDps.App;

public partial class AboutWindow : Window
{
    private const string GitHubUrl = "https://github.com/tonyHan123/aion2fundps";

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{ReadAssemblyVersion()}";
    }

    /// <summary>
    /// Pulls the informational version (e.g., "0.1.0-alpha") from the entry
    /// assembly. The csproj's &lt;Version&gt; element flows into
    /// AssemblyInformationalVersionAttribute at build time, so updating one
    /// property (Aion2FunDps.App.csproj) is enough — no scattered constants
    /// to keep in sync.
    /// </summary>
    private static string ReadAssemblyVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (info?.InformationalVersion is { Length: > 0 } v)
        {
            // Strip the trailing "+sha" git hash that .NET appends automatically.
            int plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
        return asm?.GetName().Version?.ToString() ?? "?";
    }

    private void GitHub_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GitHubUrl,
                UseShellExecute = true,
            });
        }
        catch { /* browser launch failure is non-fatal */ }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
