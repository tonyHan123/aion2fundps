using System.Windows;

namespace Aion2FunDps.App;

/// <summary>
/// Swaps the active visual theme at runtime by replacing the first entry of
/// <see cref="Application.Resources"/>'s MergedDictionaries with the chosen
/// theme's ResourceDictionary. All consumers in MainWindow.xaml reference
/// theme keys via DynamicResource, so the swap propagates instantly without
/// any window restart or per-control rebinding.
///
/// Theme contract: every theme dictionary must define the same set of keys
/// (BgBrush, TitleBarBrush, TextBrush, etc.) so DynamicResource lookups
/// resolve in every theme. See Themes/Theme.Default.xaml for the canonical
/// key list.
/// </summary>
public static class ThemeManager
{
    public sealed record ThemeOption(string Id, string DisplayName, string Description);

    /// <summary>
    /// User-selectable themes. Order here matches the order shown in the
    /// settings panel. New themes append to this list and ship as a new
    /// Themes/Theme.{Id}.xaml file.
    /// </summary>
    public static readonly IReadOnlyList<ThemeOption> AvailableThemes = new[]
    {
        new ThemeOption("Default",     "다크+블루 (기본)", "다크+블루 톤"),
        new ThemeOption("SweetPastel", "Sweet Pastel",     "크림 베이스에 핑크 액센트"),
        new ThemeOption("RoseQuartz",  "Rose Quartz",      "여성스러운 핑크 톤 — 라즈베리 액센트"),
    };

    private static string _currentThemeId = "Default";
    public static string CurrentThemeId => _currentThemeId;

    /// <summary>
    /// Loads the named theme dictionary from /Themes/Theme.{themeId}.xaml
    /// and swaps it into Application.Resources at slot 0. Falls back to
    /// Default silently if the requested theme is missing — corrupted or
    /// outdated settings shouldn't block app startup.
    /// </summary>
    public static void ApplyTheme(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId)) themeId = "Default";

        var app = Application.Current;
        if (app == null) return;

        var uri = new Uri($"/Themes/Theme.{themeId}.xaml", UriKind.Relative);
        ResourceDictionary newDict;
        try
        {
            newDict = (ResourceDictionary)Application.LoadComponent(uri);
        }
        catch
        {
            // Unknown theme id — fall back to Default. We always know
            // Default exists (shipped with the app), so this branch can't
            // recurse into another fallback.
            if (themeId == "Default") return;
            ApplyTheme("Default");
            return;
        }

        // Swap by reference at slot 0. Application.Resources.MergedDictionaries
        // is the parent dictionary that the per-window MainWindow.xaml's
        // MergedDictionaries inherits from, so updating it here flips every
        // open window simultaneously.
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(newDict);
        }
        else
        {
            app.Resources.MergedDictionaries[0] = newDict;
        }
        _currentThemeId = themeId;
    }
}
