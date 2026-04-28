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

            Log("Init NpcapAdapter");
            _capture = new NpcapAdapter(new CaptureOptions());
            Log($"  selected: {_capture.SelectedInterface}");

            var reorderer = new SequenceReorderer();
            var assembler = new FrameAssembler();
            var dispatcher = new PacketDispatcher();
            var aggregator = new DpsAggregator();

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

            Log("Creating MainViewModel");
            var vm = new MainViewModel(aggregator, _capture, assembler, dispatcher, skillDb);

            Log("Creating MainWindow");
            var window = new MainWindow { DataContext = vm };

            Log("Showing window");
            window.Show();
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
        _cts?.Cancel();
        _capture?.Dispose();
        base.OnExit(e);
    }
}
