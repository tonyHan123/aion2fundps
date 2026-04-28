using System.Net;
using System.Net.Sockets;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Aion 2 — Hex Dump PoC ===");
Console.WriteLine();

const string GameServerIp = "206.127.156.161";

// 알려진 KR 서버 opcode (TK-open-public 분석 결과)
var knownOpcodes = new (byte, byte, string)[]
{
    (0x04, 0x38, "DAMAGE 데미지"),
    (0x05, 0x38, "DOT 도트"),
    (0x40, 0x36, "SUMMON_SPAWN 소환수"),
    (0x33, 0x36, "SELF_NICK 본인닉"),
    (0x44, 0x36, "OTHER_NICK 타인닉"),
    (0x21, 0x8d, "COMBAT_BOUNDARY 전투경계"),
    (0x00, 0x8d, "MOB_HP 몹HP"),
    (0x2a, 0x38, "BUFF_APPLY 버프적용"),
    (0x2b, 0x38, "BUFF_REMOVE 버프해제"),
};

// 1) NIC 자동 선택
var devices = CaptureDeviceList.Instance;
LibPcapLiveDevice? selected = null;

try
{
    using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
    probe.Connect(GameServerIp, 7777);
    var localIp = ((IPEndPoint)probe.LocalEndPoint!).Address;
    var b = localIp.GetAddressBytes();
    if (!(b[0] == 169 && b[1] == 254))
    {
        foreach (var d in devices.OfType<LibPcapLiveDevice>())
            if (d.Addresses.Any(a => a.Addr?.ipAddress?.Equals(localIp) == true))
            { selected = d; Console.WriteLine($"자동 선택: {d.Description} (로컬 IP: {localIp})"); break; }
    }
}
catch { }

if (selected == null)
{
    Console.WriteLine("자동 선택 실패. 인덱스 입력:");
    for (int i = 0; i < devices.Count; i++) Console.WriteLine($"  [{i}] {devices[i].Description}");
    Console.Write("> ");
    if (int.TryParse(Console.ReadLine(), out int idx) && idx >= 0 && idx < devices.Count)
        selected = (LibPcapLiveDevice)devices[idx];
    else { Console.WriteLine("종료."); return; }
}

// 2) 게임 서버 inbound TCP만 캡처
selected.Open(new DeviceConfiguration
{
    Mode = DeviceModes.Promiscuous,
    ReadTimeout = 100,
    Snaplen = 65535,
    BufferSize = 64 * 1024 * 1024
});
selected.Filter = $"src host {GameServerIp} and tcp";

Console.WriteLine();
Console.WriteLine($"필터: src host {GameServerIp} and tcp");
Console.WriteLine($"게임 서버 → 본인 PC 방향 TCP 패킷만 캡처");
Console.WriteLine();

const int MaxPackets = 20;
int packetNum = 0;
var done = new ManualResetEventSlim(false);

selected.OnPacketArrival += (_, e) =>
{
    if (Volatile.Read(ref packetNum) >= MaxPackets) return;

    var raw = e.GetPacket();
    var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
    var tcp = packet.Extract<TcpPacket>();
    var ip = packet.Extract<IPv4Packet>();
    if (tcp == null || ip == null) return;

    var payload = tcp.PayloadData;
    if (payload == null || payload.Length == 0) return;  // SYN/ACK 등 제어 패킷 스킵

    int n = Interlocked.Increment(ref packetNum);

    Console.WriteLine();
    Console.WriteLine($"━━━ [{n,2}/{MaxPackets}] {ip.SourceAddress}:{tcp.SourcePort} → :{tcp.DestinationPort}, {payload.Length} bytes ━━━");

    int len = Math.Min(payload.Length, 96);
    for (int row = 0; row < (len + 15) / 16; row++)
    {
        int start = row * 16;
        int end = Math.Min(start + 16, len);
        var hex = string.Join(" ", payload.Skip(start).Take(end - start).Select(b => b.ToString("x2")));
        var ascii = new string(payload.Skip(start).Take(end - start)
            .Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
        Console.WriteLine($"  {start:x4}  {hex,-48}  {ascii}");
    }
    if (payload.Length > 96) Console.WriteLine($"  ... +{payload.Length - 96} bytes more");

    // LZ4 압축 마커 검사
    if (payload.Length >= 2 && payload[0] == 0xff && payload[1] == 0xff)
        Console.WriteLine("  🗜️  offset 0: 0xff 0xff = LZ4 압축 마커");

    // 알려진 opcode 검사 (앞 24바이트 안에서 매칭)
    int scanLen = Math.Min(payload.Length - 1, 24);
    for (int i = 0; i < scanLen; i++)
    {
        foreach (var (b1, b2, name) in knownOpcodes)
        {
            if (payload[i] == b1 && payload[i + 1] == b2)
                Console.WriteLine($"  ✨ offset {i,2}: 0x{b1:x2} 0x{b2:x2} = {name}");
        }
    }

    if (n >= MaxPackets) done.Set();
};

selected.StartCapture();

Console.WriteLine($"게임 서버 패킷 {MaxPackets}개 캡처 (최대 60초 대기)");
Console.WriteLine("→ 게임 안에서 던전/전투/이동 등 활동하세요");

done.Wait(TimeSpan.FromSeconds(60));
selected.StopCapture();

Console.WriteLine();
Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
Console.WriteLine($"=== 캡처 종료 ===");
Console.WriteLine($"수집한 게임 패킷: {packetNum}/{MaxPackets}");
var stats = selected.Statistics;
Console.WriteLine($"드랍: 커널 {stats.DroppedPackets}, 인터페이스 {stats.InterfaceDroppedPackets}");
selected.Close();

Console.WriteLine();
Console.WriteLine("아무 키나 누르면 닫힙니다.");
Console.ReadKey();
