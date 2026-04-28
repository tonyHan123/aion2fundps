using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Core.Models;
using Aion2FunDps.Protocol;
using Aion2FunDps.Storage.Databases;

var options = new CaptureOptions();

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Load static game data
MobDatabase mobDb;
SkillDatabase skillDb;
try
{
    mobDb = JsonDataLoader.LoadMobDatabase();
    skillDb = JsonDataLoader.LoadSkillDatabase();
}
catch (Exception ex)
{
    Console.WriteLine($"❌ 데이터 로드 실패: {ex.Message}");
    return;
}

NpcapAdapter capture;
try
{
    capture = new NpcapAdapter(options);
}
catch (Exception ex)
{
    Console.WriteLine($"❌ 캡처 초기화 실패: {ex.Message}");
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
            Console.Clear();
            Console.CursorVisible = false;

            // Refresh accuracy estimator with latest counters from upstream subsystems
            aggregator.RefreshAccuracy(
                droppedPackets: capture.Health.DroppedPackets + capture.Health.InterfaceDroppedPackets + capture.Health.DroppedAtChannel,
                malformedFrames: assembler.MalformedFrames + dispatcher.MalformedCount,
                unknownOpcodes: dispatcher.UnknownCount);

            Console.WriteLine("=== Aion2FunDps DevConsole — Phase 1e: 신뢰도 배지 ===");
            Console.WriteLine();
            Console.WriteLine($"인터페이스: {capture.SelectedInterface}");
            Console.WriteLine($"DB: 몹 {mobDb.Count:N0}개  /  스킬 {skillDb.Count:N0}개  /  세션 {(int)aggregator.Current.Duration.TotalSeconds}s");

            // Confidence badge — the differentiator
            var acc = aggregator.Accuracy;
            var issues = string.Join(", ", acc.Issues());
            if (string.IsNullOrEmpty(issues)) issues = "(이상 없음)";
            Console.WriteLine($"📊 신뢰도: {acc.StatusEmoji} {acc.ConfidenceScore,4:P0} ({acc.Tier})  └ {issues}");

            // Boss banner
            if (aggregator.Boss.IsBossMode && aggregator.Boss.FocusedEntityId.HasValue)
            {
                var bossId = aggregator.Boss.FocusedEntityId.Value;
                var entity = aggregator.Boss.GetEntity(bossId)!;
                double pct = entity.MaxHp > 0 ? (double)entity.CurrentHp / entity.MaxHp * 100 : 0;
                Console.WriteLine();
                Console.WriteLine($"⚔️  보스 전투 — Entity_{bossId}  HP: {entity.CurrentHp,12:N0} / {entity.MaxHp,12:N0}  ({pct,5:F1}% 남음)");
            }
            else if (aggregator.Boss.FocusedEntityId.HasValue)
            {
                var bossId = aggregator.Boss.FocusedEntityId.Value;
                var entity = aggregator.Boss.GetEntity(bossId)!;
                Console.WriteLine();
                Console.WriteLine($"   필드 사냥 모드 — 주 타겟 Entity_{bossId} (max HP {entity.MaxHp:N0}, < 보스 임계값 500K)");
            }

            Console.WriteLine();
            Console.WriteLine("  순위 닉네임/ID         총데미지       DPS     히트   크리%   백%");
            Console.WriteLine("  ──── ────────────── ──────────── ──────── ──────── ────── ──────");

            var crew = aggregator.OurCrew()
                                 .OrderByDescending(p => p.TotalDamage)
                                 .Take(MaxRows)
                                 .ToList();

            int rank = 0;
            foreach (var p in crew)
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

            int totalActors = aggregator.Current.PlayerCount;
            int crewCount = crew.Count;
            int filtered = totalActors - crewCount;

            Console.WriteLine();
            Console.WriteLine($"  Σ 우리 파티: {crew.Sum(p => p.TotalDamage),12:N0}    파티원: {crewCount,2}명    " +
                              $"필터링됨: {filtered}명 (랜덤/몹)");
            Console.WriteLine($"  이벤트: dmg={aggregator.DamageEventCount} dot={aggregator.DotEventCount} " +
                              $"hp={aggregator.HpEventCount} nick={aggregator.NicknameEventCount} " +
                              $"summon={aggregator.SummonSpawnEventCount}");
            Console.WriteLine($"  소환수 재귀속: {aggregator.ReattributedDamageCount}회 / 등록 {aggregator.Summons.Count}개  |  " +
                              $"닉네임 등록: 본인={(aggregator.Registry.SelfUserId.HasValue ? "✓" : "X")} " +
                              $"전체={aggregator.Registry.All.Count()}");
        }
        catch { }
    }
}

using (capture)
{
    capture.Start();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    using var refreshTimer = new System.Timers.Timer(1000);
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
Console.WriteLine($"전체 actors (필터 전): {aggregator.Current.PlayerCount}");
Console.WriteLine($"likely players (필터 후): {aggregator.LikelyPlayers().Count()}");

if (aggregator.Registry.SelfUserId.HasValue)
{
    var self = aggregator.Registry.GetEntry(aggregator.Registry.SelfUserId.Value);
    Console.WriteLine($"본인 캐릭터: {self?.Nickname} (server={self?.Server}, job={self?.Job})");
}

// Show top skills for top player
var top = aggregator.LikelyPlayers().OrderByDescending(p => p.TotalDamage).FirstOrDefault();
if (top != null && top.Skills.Count > 0)
{
    Console.WriteLine();
    var topName = aggregator.Registry.GetName(top.ActorId) ?? $"Actor_{top.ActorId}";
    Console.WriteLine($"=== {topName} 스킬 분석 ===");
    foreach (var s in top.Skills.Values.OrderByDescending(s => s.TotalDamage).Take(8))
    {
        var info = skillDb.Resolve((int)s.SkillCode);
        var skillName = info?.Name ?? $"Skill_{s.SkillCode}";
        Console.WriteLine($"  {skillName,-30} {s.TotalDamage,10:N0}  ({s.HitCount,4} hits, {s.CritRate,5:P0} crit)");
    }
}

Console.WriteLine();
Console.WriteLine("아무 키나 누르면 종료...");
Console.CursorVisible = true;
Console.ReadKey();
