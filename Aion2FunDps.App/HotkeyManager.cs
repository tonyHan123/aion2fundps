using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Aion2FunDps.App;

/// <summary>
/// Registers system-wide hotkeys via Win32 RegisterHotKey. Unlike WPF's
/// InputBindings (which only fire when the host window has keyboard focus),
/// these hotkeys are intercepted by the OS regardless of which window is
/// active — so the user can press them while the game is foreground and
/// our handler still runs.
///
/// Why we need this: <see cref="MainWindow"/> uses WS_EX_NOACTIVATE so
/// clicks on the meter don't steal focus from the game. With NOACTIVATE,
/// the meter never has keyboard focus → InputBindings can never fire.
/// RegisterHotKey is the only way to surface meter shortcuts to a player
/// who's actively in-game.
///
/// Lifecycle: created lazily on first <see cref="Register"/> call (HWND must
/// exist). Unregister all on Dispose / window close — Windows will clean up
/// orphaned registrations on process exit anyway, but explicit cleanup
/// prevents stale "hotkey already registered" errors during a hot-restart.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Window _host;
    private readonly Dictionary<int, Action> _callbacks = new();
    private HwndSource? _src;
    private IntPtr _hwnd;
    private int _nextId = 1;

    public HotkeyManager(Window host)
    {
        _host = host;
    }

    /// <summary>
    /// Registers a global hotkey. Returns the registration id (use to
    /// <see cref="Unregister"/>) on success, or 0 if the hotkey is already
    /// taken by another app / window. Call <see cref="UnregisterAll"/>
    /// before re-registering when the user changes hotkey settings — the
    /// OS rejects duplicate id+combo registrations.
    /// </summary>
    public int Register(ModifierKeys mods, Key key, Action callback)
    {
        EnsureHooked();
        if (_hwnd == IntPtr.Zero) return 0;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return 0;

        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, ToWin32Mods(mods), vk))
            return 0;

        _callbacks[id] = callback;
        return id;
    }

    public void Unregister(int id)
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, id);
        _callbacks.Remove(id);
    }

    public void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var id in _callbacks.Keys.ToList())
            UnregisterHotKey(_hwnd, id);
        _callbacks.Clear();
    }

    private void EnsureHooked()
    {
        if (_src != null) return;
        var helper = new WindowInteropHelper(_host);
        _hwnd = helper.Handle;
        if (_hwnd == IntPtr.Zero) return;
        _src = HwndSource.FromHwnd(_hwnd);
        _src?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        int id = wParam.ToInt32();
        if (_callbacks.TryGetValue(id, out var cb))
        {
            try { cb(); } catch { /* swallow — UI should never crash on hotkey */ }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static uint ToWin32Mods(ModifierKeys m)
    {
        uint r = 0;
        if ((m & ModifierKeys.Alt) != 0) r |= MOD_ALT;
        if ((m & ModifierKeys.Control) != 0) r |= MOD_CONTROL;
        if ((m & ModifierKeys.Shift) != 0) r |= MOD_SHIFT;
        if ((m & ModifierKeys.Windows) != 0) r |= MOD_WIN;
        return r;
    }

    public void Dispose()
    {
        UnregisterAll();
        _src?.RemoveHook(WndProc);
        _src = null;
    }
}
