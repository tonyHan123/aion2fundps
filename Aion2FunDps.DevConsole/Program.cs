using Aion2FunDps.Capture;
using Aion2FunDps.Protocol;

var options = new CaptureOptions();

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Aion2FunDps DevConsole — Phase 1b: 시퀀스 정렬 + VarInt 프레이밍 ===");
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

// 알려진 KR opcode (TK-open-public 분석 기반)
var knownOpcodes = new Dictionary<(byte, byte), string>
{
    { (0x04, 0x38), "DAMAGE" },
    { (0x05, 0x38), "DOT" },
    { (0x40, 0x36), "SUMMON_SPAWN" },
    { (0x33, 0x36), "SELF_NICK" },
    { (0x44, 0x36), "OTHER_NICK" },
    { (0x21, 0x8d), "COMBAT_BOUNDARY" },
    { (0x00, 0x8d), "MOB_HP" },
    { (0x2a, 0x38), "BUFF_APPLY" },
    { (0x2b, 0x38), "BUFF_REMOVE" },
};

var reorderer = new SequenceReorderer();
var assembler = new FrameAssembler();

int totalGamePackets = 0;
int compressedCount = 0;
var opcodeStats = new Dictionary<string, int>();
const int DetailDisplayCount = 15;

using (capture)
{
    Console.WriteLine($"선택된 인터페이스: {capture.SelectedInterface}");
    Console.WriteLine($"BPF 필터: {options.BpfFilter}");
    Console.WriteLine();

    capture.Health.DropDetected += (_, args) =>
        Console.WriteLine($"⚠️  DROP +{args.NewDrops}");

    capture.Start();
    Console.WriteLine("캡처 시작. 게임에서 활동하세요. (60초 후 자동 종료, Ctrl+C로 조기 종료)");
    Console.WriteLine();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Action<GamePacket> onGamePacket = gp =>
    {
        int n = ++totalGamePackets;
        var key = (gp.OpcodeFirst, gp.OpcodeSecond);
        var name = knownOpcodes.GetValueOrDefault(key, "UNKNOWN");

        bool isCompressed = CompressionDetector.IsCompressed(gp.Data, out int compOffset);
        if (isCompressed) compressedCount++;

        var statsKey = isCompressed ? "(compressed)" : name;
        opcodeStats[statsKey] = opcodeStats.GetValueOrDefault(statsKey, 0) + 1;

        if (n <= DetailDisplayCount)
        {
            string label = isCompressed ? $"🗜️ COMPRESSED (data @ offset {compOffset})" : $"{gp.OpcodeHex} = {name}";
            Console.WriteLine($"[게임패킷 {n,3}] len={gp.Length,4}, {label}");

            int previewLen = Math.Min(24, gp.Length);
            var hex = string.Empty;
            for (int i = 0; i < previewLen; i++)
                hex += gp.Buffer[i].ToString("x2") + " ";
            Console.WriteLine($"          {hex}");
        }
        else if (n % 50 == 0)
        {
            Console.Write($"\r수집 게임 패킷: {n} (압축 {compressedCount})  ");
        }

        gp.Dispose();
    };

    Action<OrderedChunk> onOrderedChunk = chunk => assembler.Feed(chunk, onGamePacket);

    try
    {
        await foreach (var rawPacket in capture.Reader.ReadAllAsync(cts.Token))
        {
            reorderer.Feed(rawPacket, onOrderedChunk);
            // ownership transferred to reorderer; do not Dispose rawPacket here
        }
    }
    catch (OperationCanceledException) { }

    capture.Stop();
}

Console.WriteLine();
Console.WriteLine();
Console.WriteLine("=== 결과 ===");
Console.WriteLine($"총 게임 패킷 추출: {totalGamePackets}");
Console.WriteLine($"  - 일반: {totalGamePackets - compressedCount}");
Console.WriteLine($"  - LZ4 압축: {compressedCount}");
Console.WriteLine($"시퀀스 정렬:");
Console.WriteLine($"  - flow 수 (4-tuple): {reorderer.FlowCount}");
Console.WriteLine($"  - retransmit drop: {reorderer.DroppedRetransmits}");
Console.WriteLine($"  - hold overflow drop: {reorderer.DroppedOverflow}");
Console.WriteLine($"프레이밍:");
Console.WriteLine($"  - flow 수: {assembler.FlowCount}");
Console.WriteLine($"  - malformed: {assembler.MalformedFrames}");

Console.WriteLine();
Console.WriteLine("=== Opcode 분포 ===");
foreach (var kv in opcodeStats.OrderByDescending(x => x.Value))
    Console.WriteLine($"  {kv.Key,-20} {kv.Value,5}");

Console.WriteLine();
Console.WriteLine("아무 키나 누르면 종료...");
Console.ReadKey();
