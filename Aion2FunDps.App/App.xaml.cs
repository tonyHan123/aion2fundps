using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Protocol;
using Aion2FunDps.Storage.Databases;
using Aion2FunDps.UI.ViewModels;

namespace Aion2FunDps.App;

public partial class App : Application
{
    private NpcapAdapter? _capture;
    private CancellationTokenSource? _cts;
    // Held as a field, not a local — Timer would be eligible for GC the moment
    // OnStartup returns, leaving the heartbeat dead silently (saw this on the
    // first deploy: "session start" line was the only entry in capture-health.log).
    private System.Threading.Timer? _healthHeartbeatTimer;
    private ForegroundWatcher? _foregroundWatcher;
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

        // Self-cleanup: kill any prior aion2fundps instance left behind by an
        // old build whose Close-handler wasn't wired up. Only matches our exact
        // window title so the dotnet build server / VS Code language server
        // (also dotnet.exe) are untouched.
        try
        {
            int myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("dotnet"))
            {
                if (p.Id == myPid) continue;
                if (p.MainWindowTitle == "aion2fundps")
                {
                    Log($"  killing prior meter instance pid={p.Id}");
                    p.Kill();
                }
            }
        }
        catch (Exception ex) { Log($"prior-instance cleanup failed (non-fatal): {ex.Message}"); }

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

            Log("Init NpcapAdapter");
            _capture = new NpcapAdapter(new CaptureOptions())
            {
                UdpDumpPath = Path.Combine(AppContext.BaseDirectory, "udp-dump.log"),
            };
            Log($"  selected: {_capture.SelectedInterface}");

            var reorderer = new SequenceReorderer();
            var assembler = new FrameAssembler();
            var profileDebugLogPath = Path.Combine(AppContext.BaseDirectory, "profile-debug.log");
            try
            {
                File.AppendAllText(profileDebugLogPath, $"=== {DateTime.Now:HH:mm:ss.fff} profile logger armed\n");
            }
            catch { }

            var dispatcher = new PacketDispatcher
            {
                DiagnosticLogPath = Path.Combine(AppContext.BaseDirectory, "nick-debug.log"),
                UnknownSnifferLogPath = Path.Combine(AppContext.BaseDirectory, "mobspawn-debug.log"),
                EncounterAnnounceLogPath = Path.Combine(AppContext.BaseDirectory, "encounter-debug.log"),
                ProfileDebugLogPath = profileDebugLogPath,
                SummonSpawnLogPath = Path.Combine(AppContext.BaseDirectory, "summon-spawn-debug.log"),
                PartyAssemblyLogPath = Path.Combine(AppContext.BaseDirectory, "party-assembly-debug.log"),
                LiveStatusLogPath = Path.Combine(AppContext.BaseDirectory, "livestatus-debug.log"),
                BulkInfoLogPath = Path.Combine(AppContext.BaseDirectory, "bulk-debug.log"),
                PartyFamilyLogPath = Path.Combine(AppContext.BaseDirectory, "party-family-debug.log"),
            };
            var aggregator = new DpsAggregator
            {
                DungeonDb = dungeonDb,
            };
            aggregator.Boss.DiagnosticLogPath = Path.Combine(AppContext.BaseDirectory, "reset-debug.log");
            aggregator.RosterDebugLogPath = Path.Combine(AppContext.BaseDirectory, "roster-debug.log");

            // Wire mob_code → boss-status predicate. Authoritative for known mobs;
            // BossTracker falls back to HP threshold when entity wasn't seen via SUMMON_SPAWN.
            aggregator.Boss.IsKnownBoss = entityId =>
            {
                int? mc = aggregator.Entities.GetMobCode(entityId);
                if (!mc.HasValue) return null;
                var info = mobDb.GetByCode(mc.Value);
                return info?.IsBoss;
            };

            _cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    Log("capture task: start");
                    _capture.Start();
                    Action<IGameEvent> onEvent = aggregator.OnEvent;
                    Action<GamePacket> onGamePacket = gp => { dispatcher.Dispatch(gp, onEvent); gp.Dispose(); };
                    Action<OrderedChunk> onOrderedChunk = chunk => assembler.Feed(chunk, onGamePacket);

                    await foreach (var rawPacket in _capture.Reader.ReadAllAsync(_cts.Token))
                        reorderer.Feed(rawPacket, onOrderedChunk);
                    Log("capture task: end (reader completed)");
                }
                catch (OperationCanceledException) { Log("capture task: canceled"); }
                catch (Exception ex) { Log($"capture task: ERROR {ex}"); }
            });

            Log("Wiring DiagnosticLogger (boss-kills.log + session-summary.log)");
            _ = new DiagnosticLogger(aggregator, dispatcher, _capture, assembler, mobDb, skillDb);

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
            var vm = new MainViewModel(aggregator, _capture, assembler, dispatcher, skillDb, mobDb);

            Log("Creating MainWindow");
            var window = new MainWindow { DataContext = vm };

            Log("Showing window");
            window.Show();

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

    protected override void OnExit(ExitEventArgs e)
    {
        Log("OnExit");
        _foregroundWatcher?.Stop();
        _cts?.Cancel();
        _capture?.Dispose();
        base.OnExit(e);
        // Force-terminate the host process. The capture task reads from a
        // bounded channel via ReadAllAsync(token) — token cancellation doesn't
        // immediately wake a blocked native packet callback, so the dotnet
        // host would otherwise linger as a zombie even after the WPF window
        // closed. Environment.Exit takes everything down deterministically.
        Environment.Exit(0);
    }
}
