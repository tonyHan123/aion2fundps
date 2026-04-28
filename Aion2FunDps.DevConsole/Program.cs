using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Core.Models;
using Aion2FunDps.Protocol;

var options = new CaptureOptions();

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Aion2FunDps DevConsole — Phase 1d: 실시간 DPS 미터 ===");
Console.WriteLine();

NpcapAdapter capture;
try
{
    capture = new NpcapAdapter(options);
}
catch (Exception ex)
{
    Console.WriteLine($"❌ 초기화 실패: {ex.Message}");
    return;
}

var reorderer = new SequenceReorderer();
var assembler = new FrameAssembler();
var dispatcher = new PacketDispatcher();
var aggregator = new DpsAggregator();

var consoleLock = new object();
const int MaxRows = 12;

void DrawStats()
{
    lock (consoleLock)
    {
        try
        {
            // ANSI: home cursor + erase from cursor down (no scroll)
            Console.Write("\x1b[H\x1b[J");
            Console.CursorVisible = false;

            // Re-draw header
            Console.WriteLine($"=== Aion2FunDps DevConsole — Phase 1d: 실시간 DPS 미터 ===");
            Console.WriteLine();
            Console.WriteLine($"인터페이스: {capture.SelectedInterface}");
            Console.WriteLine($"필터: {options.BpfFilter}");
            Console.WriteLine($"세션 시작: {aggregator.Current.StartedAt.ToLocalTime():HH:mm:ss} " +
                              $"({(int)aggregator.Current.Duration.TotalSeconds}s 경과)");
            Console.WriteLine();
            Console.WriteLine("  순위 닉네임/ID         총데미지       DPS     히트   크리%   백%");
            Console.WriteLine("  ──── ────────────── ──────────── ──────── ──────── ────── ──────");

            int rank = 0;
            foreach (var p in aggregator.Current.AllPlayers
                                    .OrderByDescending(p => p.TotalDamage)
                                    .Take(MaxRows))
            {
                rank++;
                var name = aggregator.Registry.GetName(p.ActorId) ?? $"Actor_{p.ActorId}";
                var entry = aggregator.Registry.GetEntry(p.ActorId);
                var meTag = entry?.IsSelf == true ? " [me]" : "";
                var displayName = name + meTag;
                if (displayName.Length > 14) displayName = displayName[..14];

                Console.WriteLine(
                    $"  {rank,4} {displayName,-14} {p.TotalDamage,12:N0} {p.Dps,8:F0} {p.HitCount,8} {p.CritRate,6:P0} {p.BackAttackRate,6:P0}");
            }

            Console.WriteLine();
            Console.WriteLine($"  Σ 합계: {aggregator.Current.TotalDamage,12:N0}    " +
                              $"플레이어: {aggregator.Current.PlayerCount,2}명");
            Console.WriteLine($"  이벤트: dmg={aggregator.DamageEventCount} hp={aggregator.HpEventCount} " +
                              $"nick={aggregator.NicknameEventCount} combat={aggregator.CombatBoundaryEventCount}");
            Console.WriteLine($"  Dispatcher: known={dispatcher.KnownCount} unknown={dispatcher.UnknownCount} " +
                              $"malformed={dispatcher.MalformedCount}");
        }
        catch { }
    }
}

using (capture)
{
    capture.Start();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    using var refreshTimer = new System.Timers.Timer(500);
    refreshTimer.Elapsed += (_, _) => DrawStats();
    refreshTimer.Start();

    Action<IGameEvent> onEvent = aggregator.OnEvent;
    Action<GamePacket> onGamePacket = gp => { dispatcher.Dispatch(gp, onEvent); gp.Dispose(); };
    Action<OrderedChunk> onOrderedChunk = chunk => assembler.Feed(chunk, onGamePacket);

    try
    {
        await foreach (var rawPacket in capture.Reader.ReadAllAsync(cts.Token))
            reorderer.Feed(rawPacket, onOrderedChunk);
    }
    catch (OperationCanceledException) { }

    refreshTimer.Stop();
    capture.Stop();
    DrawStats();
}

Console.WriteLine();
Console.WriteLine("=== 최종 결과 ===");
Console.WriteLine($"세션 길이: {aggregator.Current.Duration.TotalSeconds:F0}s");
Console.WriteLine($"플레이어 수: {aggregator.Current.PlayerCount}");
Console.WriteLine($"총 데미지: {aggregator.Current.TotalDamage:N0}");
if (aggregator.Current.Duration.TotalSeconds > 0)
    Console.WriteLine($"전체 평균 DPS: {aggregator.Current.TotalDamage / aggregator.Current.Duration.TotalSeconds:F0}");
Console.WriteLine();
Console.WriteLine($"이벤트 카운트: dmg={aggregator.DamageEventCount} hp={aggregator.HpEventCount} " +
                  $"nick={aggregator.NicknameEventCount} combat={aggregator.CombatBoundaryEventCount}");
Console.WriteLine($"Dispatcher: known={dispatcher.KnownCount} unknown={dispatcher.UnknownCount} malformed={dispatcher.MalformedCount}");
Console.WriteLine($"LZ4: success={dispatcher.Lz4.SuccessCount} failure={dispatcher.Lz4.FailureCount}");

if (aggregator.Registry.SelfUserId.HasValue)
{
    var self = aggregator.Registry.GetEntry(aggregator.Registry.SelfUserId.Value);
    Console.WriteLine($"본인 캐릭터: {self?.Nickname} (server={self?.Server}, job={self?.Job})");
}
else
{
    Console.WriteLine("본인 닉네임: 미발견 (캐릭터 정보창 [P] 열어보면 잡힘)");
}

Console.WriteLine();
Console.WriteLine("아무 키나 누르면 종료...");
Console.CursorVisible = true;
Console.ReadKey();
