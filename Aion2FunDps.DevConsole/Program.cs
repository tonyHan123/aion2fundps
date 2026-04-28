using Aion2FunDps.Capture;

const string GameServerIp = "206.127.156.161";

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Aion2FunDps DevConsole — Phase 1a Capture 검증 ===");
Console.WriteLine();

NpcapAdapter capture;
try
{
    capture = new NpcapAdapter(new CaptureOptions { ServerIp = GameServerIp });
}
catch (Exception ex)
{
    Console.WriteLine($"❌ 초기화 실패: {ex.Message}");
    return;
}

using (capture)
{
    Console.WriteLine($"선택된 인터페이스: {capture.SelectedInterface}");
    Console.WriteLine($"BPF 필터: src host {GameServerIp} and tcp");
    Console.WriteLine();

    capture.Health.DropDetected += (_, args) =>
        Console.WriteLine($"⚠️  DROP +{args.NewDrops} (kernel={args.TotalKernelDrops}, iface={args.TotalInterfaceDrops})");

    capture.Start();
    Console.WriteLine("캡처 시작. 게임에서 활동하세요. (60초 후 자동 종료, 또는 Ctrl+C)");
    Console.WriteLine();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    int packetCount = 0;
    long totalBytes = 0;
    var startTime = DateTime.UtcNow;

    try
    {
        await foreach (var packet in capture.Reader.ReadAllAsync(cts.Token))
        {
            packetCount++;
            totalBytes += packet.Length;

            if (packetCount <= 5)
            {
                var ip = packet.GetSourceAddress();
                Console.WriteLine($"[{packetCount}] {ip}, seq={packet.TcpSequence}, len={packet.Length} bytes");
                int previewLen = Math.Min(16, packet.Length);
                var hexBuf = new char[previewLen * 3];
                for (int i = 0; i < previewLen; i++)
                {
                    var b = packet.Payload[i];
                    hexBuf[i * 3] = "0123456789abcdef"[b >> 4];
                    hexBuf[i * 3 + 1] = "0123456789abcdef"[b & 0xF];
                    hexBuf[i * 3 + 2] = ' ';
                }
                Console.WriteLine($"     {new string(hexBuf)}");
            }
            else if (packetCount % 100 == 0)
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                Console.Write($"\r수집: {packetCount} packets, {totalBytes / 1024} KB, {packetCount / elapsed:F0} pkt/s ");
            }

            packet.Dispose();
        }
    }
    catch (OperationCanceledException) { }

    capture.Stop();
    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("=== 결과 ===");
    Console.WriteLine($"총 캡처 패킷: {packetCount}");
    Console.WriteLine($"총 바이트: {totalBytes / 1024} KB");
    Console.WriteLine($"커널 수신: {capture.Health.ReceivedPackets}");
    Console.WriteLine($"커널 드랍: {capture.Health.DroppedPackets}");
    Console.WriteLine($"인터페이스 드랍: {capture.Health.InterfaceDroppedPackets}");
    Console.WriteLine($"채널 드랍: {capture.Health.DroppedAtChannel}");

    bool clean = capture.Health.DroppedPackets == 0
              && capture.Health.InterfaceDroppedPackets == 0
              && capture.Health.DroppedAtChannel == 0;
    Console.WriteLine(clean ? "✅ 드랍 0 — 캡처 안정" : "⚠️ 드랍 발생 — 버퍼/채널 용량 검토 필요");
}

Console.WriteLine();
Console.WriteLine("아무 키나 누르면 종료...");
Console.ReadKey();
