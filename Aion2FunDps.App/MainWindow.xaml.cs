using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Aion2FunDps.App;

public partial class MainWindow : Window
{
    // Win32 extended window style flags. WS_EX_TOOLWINDOW marks the window
    // as a "palette / utility window" → Windows omits it from the Alt+Tab
    // task switcher and from screen capture / preview overlays. Combined
    // with ShowInTaskbar=False (already set) gives the typical game-overlay
    // experience: the meter is visible on screen but doesn't clutter the
    // user's window-switching workflow when Alt-tabbing between game and
    // browser / Discord / etc.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    // WS_EX_NOACTIVATE prevents the window from being activated when clicked.
    // Clicks still deliver mouse events (drag/buttons keep working) but the
    // foreground window remains whatever it was before — typically the game.
    // This is the actual fix for the "Alt+Tab gets weird after clicking meter"
    // report 2026-05-03: clicking the meter no longer makes it the active
    // window, so Alt+Tab cycles between the user's real apps without the
    // meter ever entering the picture as a "current" entry. Child dialogs
    // (Settings / About / SkillBreakdown) don't inherit this flag — they
    // open as separate Window instances with default activation, so radio
    // buttons / hotkey capture / textboxes work normally.
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public MainWindow()
    {
        InitializeComponent();

#if !DEBUG
        MarkBtn.Visibility = Visibility.Collapsed;
#endif

        // Hide from Alt+Tab — two-layer defense because WS_EX_TOOLWINDOW
        // alone isn't reliable on every Windows 10/11 build, especially
        // when the meter has been recently activated (clicked) — the modern
        // Alt+Tab UI can resurface the most-recent foreground window even
        // for tool-style windows. Layer 1: WS_EX_TOOLWINDOW on this window
        // (set in SourceInitialized below). Layer 2: this window is owned
        // by a hidden ghost window. Owned + tool-style windows are
        // unconditionally absent from Alt+Tab regardless of activation
        // history. The ghost owner is created before this constructor
        // returns so the Owner relationship is established before first
        // Show().
        var ghostOwner = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Top = -32000,
            Left = -32000,
            Opacity = 0,
            ResizeMode = ResizeMode.NoResize,
        };
        // Show() then Hide() forces the HWND to materialize so we can apply
        // WS_EX_TOOLWINDOW to the ghost itself. Without this the ghost has
        // no native handle and nothing happens.
        ghostOwner.Show();
        ghostOwner.Hide();
        var ghostHwnd = new WindowInteropHelper(ghostOwner).Handle;
        if (ghostHwnd != IntPtr.Zero)
        {
            var ex = GetWindowLongPtr(ghostHwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(ghostHwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        }
        Owner = ghostOwner;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE,
                new IntPtr(current | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        };

        // Re-apply on every activation as a third belt-and-suspenders. Some
        // Windows 11 24H2+ builds reportedly clear the flag when a tool
        // window steals focus from a fullscreen game; cheap to re-set on
        // every Activated event since it's a no-op when already set.
        Activated += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            long want = current | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (current != want)
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(want));
        };
        // Catch every termination path: custom ✕ button (CloseBtn_Click), Alt+F4,
        // system close, programmatic Close(), parent App.Shutdown(). The Closing
        // event fires for all of them. Environment.Exit takes the host process
        // down deterministically — without it the SharpPcap callback thread
        // would keep the dotnet host alive past WPF's normal shutdown.
        // Save settings BEFORE Environment.Exit because the latter bypasses
        // App.OnExit (where the canonical save would otherwise run).
        Closing += (_, _) =>
        {
            App.Instance.SnapshotAndSaveSettings();
            Environment.Exit(0);
        };

        // Block Windows Aero Snap maximize. With WindowStyle=None +
        // AllowsTransparency=True, dragging the window to the top edge still
        // triggers the OS-level "snap to maximize" behaviour, but our
        // chrome-less window has no title-bar caption to double-click for
        // un-maximize — the window appears stuck. Reverting to Normal with
        // the pre-snap size on every Maximized transition keeps the meter
        // a normal draggable window regardless of where the user drops it.
        StateChanged += OnStateChanged;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    /// Last known Normal-state size, captured on every SizeChanged that
    /// occurs while WindowState is Normal. Used to restore the meter to
    /// its previous footprint after an accidental Aero Snap maximize.
    /// </summary>
    private double _lastNormalWidth = 460;
    private double _lastNormalHeight = 320;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _lastNormalWidth = Width;
            _lastNormalHeight = Height;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            Width = _lastNormalWidth;
            Height = _lastNormalHeight;
        }
    }

    private double _expandedHeight = 320;

    // Manual drag state — replaces DragMove() so we bypass the OS-level
    // "user is dragging by titlebar" gesture that triggers Aero Snap. With
    // DragMove(), dragging the window to a screen edge or corner snaps it
    // to half/quarter-screen, which on a chrome-less + transparent window
    // looks broken (blank haze where the bottom of the window expanded
    // into). Setting Left/Top directly during MouseMove sidesteps OS snap
    // detection entirely. WindowState.Maximized handler stays as a backup
    // in case some other path (e.g., Win+Up) maximizes us.
    private bool _isDragging;
    private System.Windows.Point _mouseDownScreenPos;
    private double _windowLeftAtDown;
    private double _windowTopAtDown;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // Double-click on titlebar = reset to default 540x420 (escape hatch when the
        // window has stretched to an unusable size and the resize gripper is hidden).
        if (e.ClickCount == 2)
        {
            SizeToContent = SizeToContent.Manual;
            Width = 460;
            Height = 320;
            _expandedHeight = 320;
            return;
        }

        _isDragging = true;
        _mouseDownScreenPos = PointToScreen(e.GetPosition(this));
        _windowLeftAtDown = Left;
        _windowTopAtDown = Top;
        ((UIElement)sender).CaptureMouse();
    }

    private void TitleBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var current = PointToScreen(e.GetPosition(this));
        Left = _windowLeftAtDown + (current.X - _mouseDownScreenPos.X);
        Top = _windowTopAtDown + (current.Y - _mouseDownScreenPos.Y);
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void MinBtn_Click(object sender, RoutedEventArgs e)
    {
        // Native WindowState.Minimized + ShowInTaskbar=False creates a legacy "minimized
        // bar" stuck at the bottom-left of the screen that the user can't drag. Instead,
        // collapse the content to titlebar-only — the window stays a normal window so
        // the user can drag it anywhere.
        if (DataContext is not Aion2FunDps.UI.ViewModels.MainViewModel vm) return;

        if (!vm.IsCompact)
        {
            _expandedHeight = ActualHeight;
            vm.IsCompact = true;
            // Two things had to drop together for the dark stripe under the titlebar
            // to disappear:
            //   1. Row 3's "*" height — claims leftover space even when its child is
            //      Collapsed, exposing the outer Border's dark BgBrush as a band.
            //   2. The Window's MinHeight=60 — overrode SizeToContent.Height and
            //      forced 60px total, leaving a similar dark band below the titlebar.
            LeaderboardRow.Height = new System.Windows.GridLength(0);
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            OuterBorder.CornerRadius = new System.Windows.CornerRadius(0);
            OuterBorder.BorderThickness = new System.Windows.Thickness(0);
        }
        else
        {
            LeaderboardRow.Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
            MinHeight = 60;
            SizeToContent = SizeToContent.Manual;
            Height = _expandedHeight;
            vm.IsCompact = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            OuterBorder.CornerRadius = new System.Windows.CornerRadius(6);
            OuterBorder.BorderThickness = new System.Windows.Thickness(1);
        }
    }

    private int _markCounter;
    private System.Windows.Threading.DispatcherTimer? _markFlashTimer;
    private const string MarkBtnIdleContent = "📌";
    private static readonly string MarksLogPath =
        System.IO.Path.Combine(System.AppContext.BaseDirectory, "user-marks.log");

    private void MarkBtn_Click(object sender, RoutedEventArgs e)
    {
        _markCounter++;
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        try
        {
            System.IO.File.AppendAllText(MarksLogPath,
                $"[{stamp}] MARK #{_markCounter}\n");
        }
        catch { /* user-marks is best-effort; failure must not crash UI */ }

        MarkBtn.ToolTip = $"마지막 마크 #{_markCounter} @ {stamp}";
        MarkBtn.Content = $"#{_markCounter}";

        // Reuse a single timer rather than creating one per click. Two rapid
        // clicks would otherwise leak two timers and — because the second
        // captured the *current* (already-flashed) Content as "origContent" —
        // would leave the button stuck on a stale "#N" after both fire.
        // Restore target is a fixed constant instead of captured state.
        _markFlashTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _markFlashTimer.Stop();
        _markFlashTimer.Tick -= MarkFlashTimer_Tick;
        _markFlashTimer.Tick += MarkFlashTimer_Tick;
        _markFlashTimer.Start();
    }

    private void MarkFlashTimer_Tick(object? sender, EventArgs e)
    {
        _markFlashTimer?.Stop();
        MarkBtn.Content = MarkBtnIdleContent;
    }

    private void PlayerRow_Click(object sender, MouseButtonEventArgs e)
    {
        // The DataContext on a row's outer Grid is the PlayerRowViewModel for
        // that row. ActorId is the canonical entity id we use to look up
        // PlayerStats in the aggregator.
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not Aion2FunDps.UI.ViewModels.PlayerRowViewModel row) return;
        if (DataContext is not Aion2FunDps.UI.ViewModels.MainViewModel vm) return;

        var win = new SkillBreakdownWindow(
            vm.Aggregator, row.ActorId, row.DisplayName, row.ClassIcon)
        {
            Owner = this,
        };
        win.Show();
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        // Modal so the user can't accidentally lose the settings window
        // behind the meter while picking a theme. Owner=this anchors the
        // dialog to the meter window — closing the meter closes the dialog.
        var settings = new SettingsWindow { Owner = this };
        settings.ShowDialog();
        // Settings window may have changed hotkeys — re-apply.
        ApplyHotkeys();
    }

    private HotkeyManager? _hotkeys;

    /// <summary>
    /// Rebuilds the global hotkey registrations from current AppSettings.
    /// Called on startup and whenever the settings panel changes a hotkey.
    ///
    /// Switched from WPF InputBindings to Win32 RegisterHotKey because the
    /// meter window now has WS_EX_NOACTIVATE (so clicks don't steal focus
    /// from the game) — InputBindings need keyboard focus on the host
    /// window, which never happens for a non-activating overlay. Global
    /// hotkeys work even when the game is foreground, which is the actual
    /// use case (사용자 보고 2026-05-03: 게임 중에 작동 안 하면 의미 없음).
    /// </summary>
    public void ApplyHotkeys()
    {
        _hotkeys ??= new HotkeyManager(this);
        _hotkeys.UnregisterAll();

        var settings = App.Instance.Settings;

        if (HotkeyParser.TryParse(settings.ResetHotkey, out var resetKey, out var resetMods)
            && DataContext is Aion2FunDps.UI.ViewModels.MainViewModel vm)
        {
            _hotkeys.Register(resetMods, resetKey, () =>
            {
                if (vm.ResetSessionCommand?.CanExecute(null) == true)
                    vm.ResetSessionCommand.Execute(null);
            });
        }

        if (HotkeyParser.TryParse(settings.MinimizeHotkey, out var minKey, out var minMods))
        {
            _hotkeys.Register(minMods, minKey, () =>
                MinBtn_Click(this, new RoutedEventArgs()));
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        // Note: this path goes through the same Closing handler installed in
        // the ctor (which saves settings before Environment.Exit). Calling
        // Close() instead of Environment.Exit() routes through the framework's
        // close pipeline, which fires the Closing event so settings are saved.
        Close();
    }
}
