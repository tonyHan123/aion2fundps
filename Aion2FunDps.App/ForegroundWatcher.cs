using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Aion2FunDps.App;

/// <summary>
/// Watches Windows' foreground-window changes and fires
/// <see cref="ActiveChanged"/> when focus crosses between the watched
/// process and "anything else". The meter subscribes to this so it acts
/// as a true game overlay — visible only while Aion 2 has focus, hidden
/// the instant the user Alt-Tabs away.
///
/// Hybrid detection:
///   - <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c> fires immediately
///     on every focus change → no perceptible delay when Alt-Tabbing.
///   - A 1-second polling tick acts as a safety net for foreground
///     transitions the hook can occasionally miss (e.g. some launchers
///     use sequences of focus events the hook coalesces).
///
/// "Self-preserve" rule: when the meter window itself is foreground (e.g.,
/// the user clicked the title bar to drag it), we keep <see cref="IsActive"/>
/// at its previous value. Without this, dragging the meter would briefly
/// drop ownership to ourselves and immediately hide the window we just
/// grabbed. A2Viewer's ForegroundWatcher follows the same pattern.
/// </summary>
public sealed class ForegroundWatcher
{
    private readonly string _processName;
    private readonly int _selfPid;
    private DispatcherTimer? _safetyTimer;
    private IntPtr _hookHandle;
    private WinEventDelegate? _hookDelegate;  // GC-pinned via the field
    private GCHandle _hookDelegateHandle;
    private bool _lastActive;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public bool IsActive => _lastActive;
    public event Action<bool>? ActiveChanged;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public ForegroundWatcher(string processName)
    {
        _processName = processName;
        _selfPid = Process.GetCurrentProcess().Id;
    }

    public void Start()
    {
        // Hook MUST be installed from a thread with a message pump (the
        // WPF Dispatcher thread qualifies). WINEVENT_OUTOFCONTEXT means
        // the callback runs in our process; WINEVENT_SKIPOWNPROCESS makes
        // the OS skip foreground events triggered by ourselves so the
        // self-preserve check still wins for slider / drag clicks.
        _hookDelegate = OnForegroundChanged;
        _hookDelegateHandle = GCHandle.Alloc(_hookDelegate);
        _hookHandle = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _hookDelegate,
            idProcess: 0, idThread: 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Safety-net poll. Hook delivers within milliseconds; this just
        // catches the rare missed event (driver / RDP / locked-screen
        // edge cases). 1s is long enough that it has no perceptible
        // impact on the hook-driven path.
        _safetyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _safetyTimer.Tick += (_, _) => Recheck();
        _safetyTimer.Start();

        // Prime initial state so subscribers see correct visibility now,
        // not after the first foreground change.
        _lastActive = ReadForegroundActive();
        ActiveChanged?.Invoke(_lastActive);
    }

    public void Stop()
    {
        _safetyTimer?.Stop();
        _safetyTimer = null;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        if (_hookDelegateHandle.IsAllocated)
            _hookDelegateHandle.Free();
        _hookDelegate = null;
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // OBJID_WINDOW = 0; idChild=0 → the window itself, not a child accy
        // object. Filter so we don't react to internal focus shifts.
        if (idObject != 0 || idChild != 0) return;
        Recheck();
    }

    private void Recheck()
    {
        bool active = ReadForegroundActive();
        if (active != _lastActive)
        {
            _lastActive = active;
            ActiveChanged?.Invoke(active);
        }
    }

    private bool ReadForegroundActive()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return _lastActive;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return _lastActive;

            // Foreground is the meter itself — preserve previous state so a
            // click on the title bar / slider doesn't strobe the window off.
            if (pid == (uint)_selfPid) return _lastActive;

            string name;
            try
            {
                name = Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                return _lastActive;
            }
            return name.Equals(_processName, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(_processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return _lastActive;
        }
    }
}
