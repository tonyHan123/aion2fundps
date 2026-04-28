using System.Net;
using System.Net.Sockets;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Aion 2 Meter — SharpPcap PoC ===");
Console.WriteLine($"SharpPcap {Pcap.SharpPcapVersion}");
Console.WriteLine();

var devices = CaptureDeviceList.Instance;
if (devices.Count == 0)
{
    Console.WriteLine("ERROR: 캡처 가능한 인터페이스가 없습니다. Npcap 상태를 확인하세요.");
    return;
}

Console.WriteLine($"발견된 인터페이스: {devices.Count}개");
for (int i = 0; i < devices.Count; i++)
{
    var d = devices[i];
    Console.WriteLine($"  [{i}] {d.Description}");
    if (d is LibPcapLiveDevice lpd)
    {
        var ipv4s = lpd.Addresses
            .Where(a => a.Addr?.ipAddress != null && a.Addr.ipAddress.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Addr!.ipAddress!.ToString())
            .ToList();
        if (ipv4s.Count > 0)
            Console.WriteLine($"        IPv4: {string.Join(", ", ipv4s)}");
        else
            Console.WriteLine($"        (IPv4 주소 없음)");
    }
}
Console.WriteLine();

// "Phone home" 트릭으로 게임 트래픽 흐를 NIC 자동 감지
// 실제 송신 X — OS 라우팅 테이블만 조회
Console.Write("타겟 IP 입력 (게임 서버 IP 모르면 그냥 엔터 → 8.8.8.8 사용): ");
var targetIp = Console.ReadLine()?.Trim();
if (string.IsNullOrEmpty(targetIp)) targetIp = "8.8.8.8";

IPAddress? localIp = null;
try
{
    using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
    probe.Connect(targetIp, 53);
    localIp = ((IPEndPoint)probe.LocalEndPoint!).Address;
    Console.WriteLine($"OS가 {targetIp}로 향하는 트래픽에 사용하는 로컬 IP: {localIp}");

    // APIPA(169.254.x.x) 또는 0.0.0.0 자동 거부
    var b = localIp.GetAddressBytes();
    if (b[0] == 169 && b[1] == 254)
    {
        Console.WriteLine($"⚠️ {localIp}는 APIPA 주소 (DHCP 미응답). 자동선택 비활성, 직접 인덱스 입력하세요.");
        localIp = null;
    }
    else if (localIp.Equals(IPAddress.Any))
    {
        Console.WriteLine("⚠️ 로컬 IP 결정 실패. 직접 인덱스 입력하세요.");
        localIp = null;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"라우팅 프로브 실패: {ex.Message}");
}

LibPcapLiveDevice? selected = null;
if (localIp != null)
{
    foreach (var d in devices.OfType<LibPcapLiveDevice>())
    {
        if (d.Addresses.Any(a => a.Addr?.ipAddress?.Equals(localIp) == true))
        {
            selected = d;
            Console.WriteLine($"자동 선택: {d.Description}");
            break;
        }
    }
}

if (selected == null)
{
    Console.Write("인터페이스 인덱스 입력: ");
    if (int.TryParse(Console.ReadLine(), out int idx) && idx >= 0 && idx < devices.Count)
        selected = (LibPcapLiveDevice)devices[idx];
    else { Console.WriteLine("잘못된 입력."); return; }
}

selected.Open(new DeviceConfiguration
{
    Mode = DeviceModes.Promiscuous,
    ReadTimeout = 100,
    Snaplen = 65535,
    BufferSize = 64 * 1024 * 1024  // 64MB — 누수 방지
});

Console.WriteLine();
Console.WriteLine($"캡처 시작: {selected.Description}");
Console.WriteLine("설정: Promiscuous, Snaplen 65535, Buffer 64MB");
Console.WriteLine();

int total = 0, tcp = 0;
long bytes = 0;
var srcIps = new Dictionary<string, int>();
var lockObj = new object();

selected.OnPacketArrival += (_, e) =>
{
    var raw = e.GetPacket();
    Interlocked.Increment(ref total);
    Interlocked.Add(ref bytes, raw.Data.Length);
    try
    {
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var ip = packet.Extract<IPv4Packet>();
        var t = packet.Extract<TcpPacket>();
        if (t != null && ip != null)
        {
            Interlocked.Increment(ref tcp);
            var key = ip.SourceAddress.ToString();
            lock (lockObj) srcIps[key] = srcIps.GetValueOrDefault(key) + 1;
        }
    }
    catch { }
};

selected.StartCapture();
Console.WriteLine("30초간 캡처합니다. 게임 켜고 전투/이동 등 트래픽 발생시키세요.");
Console.WriteLine();

for (int i = 30; i > 0; i--)
{
    Console.Write($"\r{i,2}s  total: {total,-8} tcp: {tcp,-8} bytes: {bytes / 1024,-6}KB ");
    Thread.Sleep(1000);
}
Console.WriteLine();
selected.StopCapture();

var stats = selected.Statistics;
Console.WriteLine();
Console.WriteLine("=== 캡처 통계 ===");
Console.WriteLine($"커널이 받은 패킷: {stats.ReceivedPackets}");
Console.WriteLine($"드랍 (커널 버퍼): {stats.DroppedPackets}");
Console.WriteLine($"드랍 (인터페이스): {stats.InterfaceDroppedPackets}");
Console.WriteLine(stats.DroppedPackets == 0 ? "✅ 드랍 없음 — 캡처 정상" : "⚠️  드랍 발생 — 버퍼/필터 조정 필요");

Console.WriteLine();
Console.WriteLine("=== 상위 송신 IP ===");
foreach (var kv in srcIps.OrderByDescending(x => x.Value).Take(10))
    Console.WriteLine($"  {kv.Key,-20} {kv.Value} packets");

selected.Close();
Console.WriteLine();
Console.WriteLine("종료. 아무 키나 누르면 닫힙니다.");
Console.ReadKey();
