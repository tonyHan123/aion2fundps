using System.Windows;
using System.Windows.Input;

namespace Aion2FunDps.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Catch every termination path: custom ✕ button (CloseBtn_Click), Alt+F4,
        // system close, programmatic Close(), parent App.Shutdown(). The Closing
        // event fires for all of them. Environment.Exit takes the host process
        // down deterministically — without it the SharpPcap callback thread
        // would keep the dotnet host alive past WPF's normal shutdown.
        Closing += (_, _) => Environment.Exit(0);

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

    private int _markCount;
    private static readonly string MarkLogPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "user-marks.log");

    private void MarkBtn_Click(object sender, RoutedEventArgs e)
    {
        _markCount++;
        try
        {
            System.IO.File.AppendAllText(MarkLogPath,
                $"MARK #{_markCount} at {DateTime.Now:HH:mm:ss.fff}\n");
        }
        catch { }
        MarkBtn.Content = $"⚑ {_markCount}";
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        // Force-terminate immediately. WPF's normal close → App.OnExit chain
        // doesn't reliably wake the SharpPcap native packet callback that the
        // capture task is blocked on, so the dotnet host would linger as a
        // zombie. Environment.Exit takes everything down deterministically
        // without waiting for the capture task to ack cancellation.
        Environment.Exit(0);
    }
}
