using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Aion2FunDps.App;

public partial class SettingsWindow : Window
{
    /// <summary>
    /// Display item bound to a single radio button in the theme list.
    /// IsSelected drives the radio's IsChecked TwoWay binding so clicking
    /// either the button or its label flips the selection.
    /// </summary>
    public sealed class ThemeListItem : INotifyPropertyChanged
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<ThemeListItem> _items;

    /// <summary>
    /// Identifies which hotkey button is currently capturing input.
    /// null = no capture in progress.
    /// </summary>
    private Button? _capturingButton;

    public SettingsWindow()
    {
        InitializeComponent();

        var current = ThemeManager.CurrentThemeId;
        _items = ThemeManager.AvailableThemes
            .Select(t => new ThemeListItem
            {
                Id = t.Id,
                DisplayName = t.DisplayName,
                Description = t.Description,
                IsSelected = t.Id == current,
            })
            .ToList();
        ThemeList.ItemsSource = _items;

        // Reflect persisted hotkeys on the buttons.
        var settings = App.Instance.Settings;
        ResetHotkeyBtn.Content = HotkeyDisplay(settings.ResetHotkey);
        MinimizeHotkeyBtn.Content = HotkeyDisplay(settings.MinimizeHotkey);

        // Capture key presses at the window level when in capture mode so
        // keys like F1-F12 aren't swallowed by the focused button's default
        // handling. Bound at PreviewKeyDown so we see the raw event before
        // any control's KeyDown logic.
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    private static string HotkeyDisplay(string? hotkey) =>
        string.IsNullOrWhiteSpace(hotkey) ? "(없음)" : hotkey;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void AboutBtn_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string themeId) return;
        if (themeId == ThemeManager.CurrentThemeId) return;

        ThemeManager.ApplyTheme(themeId);

        var applied = ThemeManager.CurrentThemeId;
        App.Instance.Settings.SelectedTheme = applied;
        App.Instance.Settings.Save();

        foreach (var item in _items) item.IsSelected = item.Id == applied;
    }

    /// <summary>
    /// Click on either hotkey button enters capture mode. The next key press
    /// (handled in PreviewKeyDown) becomes the binding, then we save + apply
    /// to the main window.
    /// </summary>
    private void HotkeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        // Cancel any other active capture first.
        if (_capturingButton != null && _capturingButton != btn)
            CancelCapture();

        _capturingButton = btn;
        btn.Content = "키 입력 대기...";
        btn.Focus();
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingButton is null) return;

        // System keys (Alt + something) come through as Key.System; the real
        // key is in e.SystemKey. Normalize so Alt+F8 captures cleanly.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // Esc cancels capture without changing the binding.
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            CancelCapture();
            e.Handled = true;
            return;
        }

        // Backspace clears the binding.
        if (key == Key.Back && mods == ModifierKeys.None)
        {
            CommitCapture(null);
            e.Handled = true;
            return;
        }

        // Bare modifier presses (Ctrl alone, etc.) don't form a valid hotkey
        // — wait for the real key.
        if (HotkeyParser.IsModifierKey(key)) return;

        var formatted = HotkeyParser.Format(key, mods);
        CommitCapture(formatted);
        e.Handled = true;
    }

    private void CancelCapture()
    {
        if (_capturingButton is null) return;
        var settings = App.Instance.Settings;
        var stored = (string)_capturingButton.Tag switch
        {
            "Reset"    => settings.ResetHotkey,
            "Minimize" => settings.MinimizeHotkey,
            _          => null,
        };
        _capturingButton.Content = HotkeyDisplay(stored);
        _capturingButton = null;
    }

    private void CommitCapture(string? hotkey)
    {
        if (_capturingButton is null) return;
        var settings = App.Instance.Settings;
        switch ((string)_capturingButton.Tag)
        {
            case "Reset":    settings.ResetHotkey = hotkey; break;
            case "Minimize": settings.MinimizeHotkey = hotkey; break;
        }
        settings.Save();

        _capturingButton.Content = HotkeyDisplay(hotkey);
        _capturingButton = null;

        // Apply immediately to the main window so the user can test the new
        // binding without re-opening settings.
        if (Owner is MainWindow mw) mw.ApplyHotkeys();
    }
}
