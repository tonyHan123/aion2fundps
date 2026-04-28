using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Core.Models;
using Aion2FunDps.Protocol;

var options = new CaptureOptions();

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Aion2FunDps DevConsole — Phase 1c: opcode dispatch + 도메인 이벤트 ===");
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

int damageCount = 0;
int hpCount = 0;
int totalDamageSum = 0;
int displayed = 0;
const int MaxDisplay = 20;

using (capture)
{
    Console.WriteLine($"선택된 인터페이스: {capture.SelectedInterface}");
    Console.WriteLine($"BPF 필터: {options.BpfFilter}");
    Console.WriteLine();

    capture.Health.DropDetected += (_, args) =>
        Console.WriteLine($"⚠️  DROP +{args.NewDrops}");

    capture.Start();
    Console.WriteLine("캡처 시작. 게임에서 전투하세요. (60초 후 자동 종료, Ctrl+C로 조기 종료)");
    Console.WriteLine();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Action<IGameEvent> onEvent = evt =>
    {
        switch (evt)
        {
            case DamageEvent dmg:
                damageCount++;
                totalDamageSum += dmg.Damage;
                if (displayed < MaxDisplay)
                {
                    displayed++;
                    string critTag = dmg.IsCritical ? " 💥CRIT" : "";
                    string backTag = dmg.IsBackAttack ? " 🗡️BACK" : "";
                    string skillTag = $"skill={dmg.SkillCode}";
                    Console.WriteLine(
                        $"[DMG ] actor={dmg.ActorId,8} → target={dmg.TargetId,8}, dmg={dmg.Damage,7} ({skillTag}){critTag}{backTag}");
                }
                break;

            case MobHpUpdate hp:
                hpCount++;
                if (displayed < MaxDisplay)
                {
                    displayed++;
                    Console.WriteLine($"[HP  ] mob={hp.MobId,8}, currentHp={hp.CurrentHp,12:N0}");
                }
                break;
        }
    };

    Action<GamePacket> onGamePacket = gp =>
    {
        dispatcher.Dispatch(gp, onEvent);
        gp.Dispose();
    };

    Action<OrderedChunk> onOrderedChunk = chunk => assembler.Feed(chunk, onGamePacket);

    try
    {
        await foreach (var rawPacket in capture.Reader.ReadAllAsync(cts.Token))
        {
            reorderer.Feed(rawPacket, onOrderedChunk);
        }
    }
    catch (OperationCanceledException) { }

    capture.Stop();
}

Console.WriteLine();
Console.WriteLine("=== 결과 ===");
Console.WriteLine($"DAMAGE 이벤트: {damageCount}");
Console.WriteLine($"  총 데미지 합: {totalDamageSum:N0}");
if (damageCount > 0)
    Console.WriteLine($"  평균 데미지: {totalDamageSum / damageCount:N0}");
Console.WriteLine($"MOB_HP 이벤트: {hpCount}");
Console.WriteLine();
Console.WriteLine($"Dispatcher 통계:");
Console.WriteLine($"  - 알려진 opcode: {dispatcher.KnownCount}");
Console.WriteLine($"  - 알려지지 않은 opcode: {dispatcher.UnknownCount}");
Console.WriteLine($"  - malformed: {dispatcher.MalformedCount}");
Console.WriteLine($"LZ4 압축 해제:");
Console.WriteLine($"  - 성공: {dispatcher.Lz4.SuccessCount}");
Console.WriteLine($"  - 실패: {dispatcher.Lz4.FailureCount}");
Console.WriteLine();
Console.WriteLine($"시퀀스 정렬:");
Console.WriteLine($"  - retransmit drop: {reorderer.DroppedRetransmits}");
Console.WriteLine($"  - hold overflow drop: {reorderer.DroppedOverflow}");
Console.WriteLine($"프레이밍 malformed: {assembler.MalformedFrames}");

Console.WriteLine();
Console.WriteLine("아무 키나 누르면 종료...");
Console.ReadKey();
