using System.Windows.Input;

namespace Aion2FunDps.App;

/// <summary>
/// Round-trips between the persisted hotkey string format ("Ctrl+R", "Alt+Shift+F8")
/// and WPF's <see cref="KeyGesture"/>. Settings store the string so users can hand-
/// edit settings.json if they ever want; binding setup uses the gesture form.
/// </summary>
public static class HotkeyParser
{
    /// <summary>
    /// Builds the display / persistence string from a key + modifiers pair.
    /// Order is fixed (Ctrl, Alt, Shift, Win) so equivalent gestures normalize
    /// to the same string regardless of which modifier the user pressed first.
    /// </summary>
    public static string Format(Key key, ModifierKeys mods)
    {
        if (key == Key.None) return string.Empty;

        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>
    /// Parses a stored hotkey string. Returns false for null/empty/malformed
    /// input so callers can treat "no binding" the same as "invalid binding".
    /// </summary>
    public static bool TryParse(string? hotkey, out Key key, out ModifierKeys mods)
    {
        key = Key.None;
        mods = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(hotkey)) return false;

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt":                  mods |= ModifierKeys.Alt;     break;
                case "shift":                mods |= ModifierKeys.Shift;   break;
                case "win": case "windows":  mods |= ModifierKeys.Windows; break;
                default: return false;
            }
        }

        if (!Enum.TryParse(parts[^1], ignoreCase: true, out key) || key == Key.None)
            return false;
        return true;
    }

    /// <summary>
    /// Filters out modifier-only key events captured during binding capture —
    /// the user pressing Ctrl alone shouldn't bind "Ctrl" as a hotkey.
    /// </summary>
    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System;
}
