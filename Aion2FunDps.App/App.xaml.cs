using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Protocol;
using Aion2FunDps.Protocol.NativeEngine;
using Aion2FunDps.Storage.Databases;
using Aion2FunDps.UI.ViewModels;

namespace Aion2FunDps.App;

public partial class App : Application
{
    // Phase 6 feature flag: switch the parsing engine from the managed
    // PacketDispatcher to the native C++ DLL adapter. Off by default while
    // the native path is under regression validation (Phase 7) — flip to
    // `true` to exercise the native engine end-to-end on a live capture.
    // Once Phase 7 confirms equivalent output the default flips to true
    // and managed becomes the fallback. `static readonly` (not `const`)
    // so flipping it for testing doesn't trigger CS0162 unreachable-code
    // warnings on the dead branch — JIT still folds it.
    private static readonly bool UseNativeEngine = false;

    private NpcapAdapter? _capture;
    private NativeEngineDispatcher? _nativeDispatcher;  // disposed on shutdown
    private CancellationTokenSource? _cts;
    // Held as a field, not a local — Timer would be eligible for GC the moment
    // OnStartup returns, leaving the heartbeat dead silently (saw this on the
    // first deploy: "session start" line was the only entry in capture-health.log).
    private System.Threading.Timer? _healthHeartbeatTimer;
    private ForegroundWatcher? _foregroundWatcher;
    private MainWindow? _mainWindow;
    public AppSettings Settings { get; private set; } = new();
    public static App Instance => (App)Current;
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "startup.log");

    static App()
    {
        try { File.WriteAllText(LogPath, $"=== {DateTime.Now:HH:mm:ss} static ctor\n"); } catch { }
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("OnStartup begin");

        // Load persisted settings BEFORE anything that depends on them
        // (theme, window position, opacity, AutoResetOnBoss). Defaults are
        // safe so a missing/corrupt file doesn't block startup.
        Settings = AppSettings.Load();
        Log($"Settings loaded: theme={Settings.SelectedTheme} opacity={Settings.WindowOpacity:F2}");

        // Apply the stored theme. ApplyTheme silently falls back to Default
        // if the theme dictionary is missing (e.g., user picked a theme that
        // was removed in a later release).
        ThemeManager.ApplyTheme(Settings.SelectedTheme);

        // Self-cleanup: kill any prior aion2fundps instance left behind by an
        // old build whose Close-handler wasn't wired up. Only matches our exact
        // window title so the dotnet build server / VS Code language server
        // (also dotnet.exe) are untouched.
        //
        // Per-process try/catch + Dispose so a single dead/zombie process
        // doesn't abort the loop early, leaving the rest of the candidates
        // un-killed. Process objects are IDisposable (hold a kernel handle).
        int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("dotnet"))
        {
            try
            {
                if (p.Id == myPid) continue;
                // MainWindowTitle access throws InvalidOperationException for
                // processes that exited between enumeration and access; the
                // outer-loop try/catch (the previous version) bailed out on
                // the first such race, leaving real zombies alive.
                if (p.MainWindowTitle == "aion2fundps")
                {
                    Log($"  killing prior meter instance pid={p.Id}");
                    p.Kill();
                }
            }
            catch (Exception ex) { Log($"  skip pid={p.Id}: {ex.Message}"); }
            finally { p.Dispose(); }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, ea) =>
        {
            Log($"UnhandledException: {ea.ExceptionObject}");
        };
        DispatcherUnhandledException += (_, ea) =>
        {
            Log($"DispatcherUnhandledException: {ea.Exception}");
            MessageBox.Show(ea.Exception.ToString(), "aion2fundps error", MessageBoxButton.OK, MessageBoxImage.Error);
            ea.Handled = true;
        };

        try
        {
            base.OnStartup(e);
            Log("base.OnStartup OK");

            Log("Loading mobs.json");
            var mobDb = JsonDataLoader.LoadMobDatabase();
            Log($"  mobs: {mobDb.Count}");

            Log("Loading skills.json");
            var skillDb = JsonDataLoader.LoadSkillDatabase();
            Log($"  skills: {skillDb.Count}");

            Log("Loading dungeons.json");
            var dungeonDb = JsonDataLoader.LoadDungeonDatabase();
            Log($"  dungeons: {dungeonDb.Count}");

            // Pre-flight Npcap check. SharpPcap's failure mode without Npcap
            // is a low-level FileNotFoundException deep inside libpcap init —
            // surface a clean dialog before that happens. Free Npcap's
            // license forbids redistribution / silent install, so we point
            // the user to npcap.com and let them install via Npcap's own UI.
            if (!NpcapDetector.IsInstalled())
            {
                Log("Npcap missing — showing install prompt");
                var dlg = new NpcapMissingWindow();
                dlg.ShowDialog();
                // Whether the user clicked "Open download page" or "Quit",
                // we exit. They restart the meter after installing Npcap.
                Shutdown(0);
                return;
            }

            Log("Init NpcapAdapter");
            _capture = new NpcapAdapter(new CaptureOptions())
            {
#if DEBUG
                UdpDumpPath = Path.Combine(AppContext.BaseDirectory, "udp-dump.log"),
#endif
            };
            Log($"  selected: {_capture.SelectedInterface}");

            var reorderer = new SequenceReorderer();
            var assembler = new FrameAssembler();

            // Per-packet diagnostic paths are gated to Debug builds. The handlers
            // and dispatcher still check `if (Path != null)` per call, so leaving
            // the property null in Release short-circuits the file I/O AND the
            // hex-dump string allocations that precede each write — the dominant
            // cost on the capture hot path. Release builds get zero diagnostic
            // I/O; user-facing logs (boss-kills, session-summary) and small
            // periodic logs (capture-health, startup) stay on.
            // Phase 6: build either the managed dispatcher or the native
            // C++ engine adapter based on UseNativeEngine. `telemetry` is
            // the counter surface that DiagnosticLogger / MainViewModel
            // read; `dispatchPacket` is the hot-path callback assembled
            // by FrameAssembler.Feed. We bind emit at this seam so the
            // capture pipeline doesn't care which engine is wired up.
            IDispatcherTelemetry telemetry;
            Action<GamePacket> dispatchPacket;
            PacketDispatcher? managedDispatcher = null;

            // Forward declaration: aggregator is constructed below; the
            // capture lambda needs its OnEvent. We assign emit after
            // aggregator is built.
            Action<IGameEvent>? onEventBound = null;
            void Emit(IGameEvent ev) => onEventBound!(ev);

            if (UseNativeEngine)
            {
                _nativeDispatcher = new NativeEngineDispatcher(Emit);
                telemetry = _nativeDispatcher;
                var native = _nativeDispatcher;
                dispatchPacket = gp => { native.Dispatch(gp); gp.Dispose(); };
            }
            else
            {
                managedDispatcher = new PacketDispatcher();
                telemetry = managedDispatcher;
                var d = managedDispatcher;
                dispatchPacket = gp => { d.Dispatch(gp, Emit); gp.Dispose(); };

#if DEBUG
                // Session header for user-marks.log so log lines from one session
                // are visually separated from the next when grepping after a bug
                // report. Other debug logs grow in-place per session — without a
                // boundary marker the user can't tell which MARK # belongs to
                // which run.
                try
                {
                    File.AppendAllText(
                        Path.Combine(AppContext.BaseDirectory, "user-marks.log"),
                        $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} session start ===\n");
                }
                catch { }

                var profileDebugLogPath = Path.Combine(AppContext.BaseDirectory, "profile-debug.log");
                try
                {
                    File.AppendAllText(profileDebugLogPath, $"=== {DateTime.Now:HH:mm:ss.fff} profile logger armed\n");
                }
                catch { }
                managedDispatcher.DiagnosticLogPath = Path.Combine(AppContext.BaseDirectory, "nick-debug.log");
                managedDispatcher.UnknownSnifferLogPath = Path.Combine(AppContext.BaseDirectory, "mobspawn-debug.log");
                managedDispatcher.EncounterAnnounceLogPath = Path.Combine(AppContext.BaseDirectory, "encounter-debug.log");
                managedDispatcher.ProfileDebugLogPath = profileDebugLogPath;
                managedDispatcher.SummonSpawnLogPath = Path.Combine(AppContext.BaseDirectory, "summon-spawn-debug.log");
                managedDispatcher.PartyAssemblyLogPath = Path.Combine(AppContext.BaseDirectory, "party-assembly-debug.log");
                managedDispatcher.LiveStatusLogPath = Path.Combine(AppContext.BaseDirectory, "livestatus-debug.log");
                managedDispatcher.BulkInfoLogPath = Path.Combine(AppContext.BaseDirectory, "bulk-debug.log");
                managedDispatcher.PartyFamilyLogPath = Path.Combine(AppContext.BaseDirectory, "party-family-debug.log");
#endif
            }
            var aggregator = new DpsAggregator
            {
                DungeonDb = dungeonDb,
            };
#if DEBUG
            aggregator.Boss.DiagnosticLogPath = Path.Combine(AppContext.BaseDirectory, "reset-debug.log");
            aggregator.RosterDebugLogPath = Path.Combine(AppContext.BaseDirectory, "roster-debug.log");
#endif

            // Wire mob_code → boss-status predicate. Three-state result:
            //   true  → known boss (mobs.json IsBoss=true)
            //   false → known mob, not a boss / known player nickname (PvP gate)
            //   null  → unknown entity, BossTracker may fall back to HP threshold
            //
            // The PvP gate (returning false instead of null when the entity is a
            // registered player) prevents 20M-HP characters in town from tripping
            // the HP-fallback boss detection in BossTracker.TryFireBossDetected,
            // which used to reset the leaderboard mid-PvP duel (audit 2026-05-04:
            // B4 high — BossTracker had no registry handle so we filter here).
            aggregator.Boss.IsKnownBoss = entityId =>
            {
                int? mc = aggregator.Entities.GetMobCode(entityId);
                if (mc.HasValue)
                {
                    var info = mobDb.GetByCode(mc.Value);
                    return info?.IsBoss;
                }
                // No mob_code, but if this entityId is a known player nickname
                // it's a PvP target — explicitly NOT a boss.
                if (aggregator.Registry.GetEntry(entityId) != null)
                    return false;
                return null;
            };

            // Now that aggregator exists, bind the emit target the
            // dispatchPacket lambda was constructed against.
            onEventBound = aggregator.OnEvent;

            _cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    Log("capture task: start");
                    _capture.Start();
                    Action<OrderedChunk> onOrderedChunk = chunk => assembler.Feed(chunk, dispatchPacket);

                    await foreach (var rawPacket in _capture.Reader.ReadAllAsync(_cts.Token))
                        reorderer.Feed(rawPacket, onOrderedChunk);
                    Log("capture task: end (reader completed)");
                }
                catch (OperationCanceledException) { Log("capture task: canceled"); }
                catch (Exception ex) { Log($"capture task: ERROR {ex}"); }
            });

            Log($"Wiring DiagnosticLogger (engine={(UseNativeEngine ? "native" : "managed")})");
            _ = new DiagnosticLogger(aggregator, telemetry, _capture, assembler, mobDb, skillDb);

            // Periodic capture-health heartbeat — writes to capture-health.log so
            // we can correlate "lobby joins missed at HH:MM" with kernel/channel
            // drops in the same window. Without this, channel saturation over a
            // long session is silent (drop counter increments but nobody reads it).
            var healthLogPath = Path.Combine(AppContext.BaseDirectory, "capture-health.log");
            try { File.AppendAllText(healthLogPath, $"=== {DateTime.Now:HH:mm:ss} session start\n"); } catch { }
            _capture.Health.DropDetected += (_, ea) =>
            {
                try
                {
                    File.AppendAllText(healthLogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} DROP +{ea.NewDrops} (kernel={ea.TotalKernelDrops} iface={ea.TotalInterfaceDrops} channel={_capture.Health.DroppedAtChannel})\n");
                }
                catch { }
            };
            _healthHeartbeatTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    File.AppendAllText(healthLogPath,
                        $"{DateTime.Now:HH:mm:ss} stats recv={_capture.Health.ReceivedPackets} kernel_drop={_capture.Health.DroppedPackets} iface_drop={_capture.Health.InterfaceDroppedPackets} channel_drop={_capture.Health.DroppedAtChannel}\n");
                }
                catch { }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            Log("Creating MainViewModel");
            var vm = new MainViewModel(aggregator, _capture, assembler, telemetry, skillDb, mobDb);

            Log("Creating MainWindow");
            var window = new MainWindow { DataContext = vm };
            _mainWindow = window;

            // Restore persisted window position / size if present. Validate
            // against current screen bounds so a stale offscreen position
            // (e.g., user disconnected an external monitor since last save)
            // can't put the meter beyond the visible work area.
            if (Settings.WindowLeft is double l && Settings.WindowTop is double t
                && l >= SystemParameters.VirtualScreenLeft
                && t >= SystemParameters.VirtualScreenTop
                && l <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
                && t <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
            {
                window.Left = l;
                window.Top = t;
            }
            if (Settings.WindowWidth is double w && w >= 200) window.Width = w;
            if (Settings.WindowHeight is double h && h >= 100) window.Height = h;

            // Restore persisted preferences onto the view-model.
            vm.WindowOpacity = Settings.WindowOpacity;
            vm.AutoResetOnBoss = Settings.AutoResetOnBoss;
            vm.IsCompact = Settings.IsCompact;

            Log("Showing window");
            window.Show();

            // Hotkeys are bound after Show() so the window has a valid
            // DataContext + handle for InputBinding registration.
            window.ApplyHotkeys();

            // Game-overlay behaviour: meter is only visible while the Aion 2
            // client is the foreground window. Alt-Tab to a browser, Discord,
            // etc. → meter hides. Returning focus to the game un-hides it.
            // Self-foreground is preserved (clicking the meter to drag it
            // doesn't trigger a hide). Process name "aion2" matches what
            // A2Viewer's OverlayForm uses against the live KR client.
            Log("Starting foreground watcher (target=aion2)");
            _foregroundWatcher = new ForegroundWatcher("aion2");
            _foregroundWatcher.ActiveChanged += active =>
            {
                if (active) window.Show();
                else        window.Hide();
            };
            _foregroundWatcher.Start();

            Log("OnStartup complete");
        }
        catch (Exception ex)
        {
            Log($"OnStartup FAILED: {ex}");
            MessageBox.Show(ex.ToString(), "aion2fundps startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Snapshots the current UI state (window geometry, theme, opacity,
    /// compact flag, auto-reset toggle) into Settings and persists to disk.
    /// Idempotent and safe to call multiple times. Must be invoked manually
    /// from MainWindow's Closing / CloseBtn paths because those use
    /// Environment.Exit to deterministically tear down the SharpPcap
    /// native callback thread, which bypasses App.OnExit. Wire-confirmed
    /// 2026-05-03: prior to this method, settings save was code-present
    /// but never actually executed at runtime — every close went through
    /// Environment.Exit and skipped OnExit entirely.
    /// </summary>
    public void SnapshotAndSaveSettings()
    {
        try
        {
            if (_mainWindow != null && _mainWindow.WindowState == WindowState.Normal)
            {
                Settings.WindowLeft   = _mainWindow.Left;
                Settings.WindowTop    = _mainWindow.Top;
                Settings.WindowWidth  = _mainWindow.Width;
                Settings.WindowHeight = _mainWindow.Height;
            }
            if (_mainWindow?.DataContext is MainViewModel vm)
            {
                Settings.WindowOpacity   = vm.WindowOpacity;
                Settings.AutoResetOnBoss = vm.AutoResetOnBoss;
                Settings.IsCompact       = vm.IsCompact;
            }
            Settings.SelectedTheme = ThemeManager.CurrentThemeId;
            Settings.Save();
            Log($"Settings saved: theme={Settings.SelectedTheme}");
        }
        catch (Exception ex)
        {
            Log($"Settings save failed (non-fatal): {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("OnExit");
        // OnExit is the "graceful WPF shutdown" path. In practice we usually
        // exit via Environment.Exit (see MainWindow), so this only runs for
        // edge-case shutdowns (e.g., Application.Shutdown called explicitly).
        // Save anyway in case we get here.
        SnapshotAndSaveSettings();

        // Dispose the heartbeat timer first so its callback can't fire
        // against an already-disposed _capture (would be a TOCTOU on
        // _capture.Health). Without this, the 30-second tick after Dispose
        // races with capture-health.log writes from the DropDetected handler.
        _healthHeartbeatTimer?.Dispose();
        _foregroundWatcher?.Stop();
        _cts?.Cancel();
        _capture?.Dispose();
        _nativeDispatcher?.Dispose();
        base.OnExit(e);
        // Force-terminate the host process. The capture task reads from a
        // bounded channel via ReadAllAsync(token) — token cancellation doesn't
        // immediately wake a blocked native packet callback, so the dotnet
        // host would otherwise linger as a zombie even after the WPF window
        // closed. Environment.Exit takes everything down deterministically.
        Environment.Exit(0);
    }
}
