namespace Aion2FunDps.Capture;

public sealed record CaptureOptions
{
    /// <summary>
    /// Any IP in the game server's subnet — used only for OS routing probe to identify the correct NIC.
    /// Does not need to be reachable; OS uses routing table to determine egress interface.
    /// </summary>
    public string RoutingProbeIp { get; init; } = "206.127.156.1";

    /// <summary>
    /// BPF filter expression. Widened to all TCP 2026-04-30 to capture lobby /
    /// matchmaking broadcasts whose server IP we haven't pinned yet. Game zone
    /// traffic is in NCSoft's /24 block but party/lobby broadcasts use a
    /// different NCSoft IP range that even /16 missed. Capturing all TCP gets
    /// noisy (browser, etc.) but our dispatcher rejects non-game frames at the
    /// frame-format level, so it's safe — just costs a bit of throughput.
    /// Refine back to a specific NCSoft CIDR once analysis pins the lobby IP.
    /// </summary>
    public string BpfFilter { get; init; } = "tcp or udp";

    public int BufferSizeBytes { get; init; } = 64 * 1024 * 1024;
    public int SnapshotLengthBytes { get; init; } = 65535;
    public int ReadTimeoutMs { get; init; } = 100;
    // 65536 (was 16384, was 4096): wire-confirmed 2026-05-03 00:33 — lobby
    // browse + room-creation produced 21K dropped packets in 30s at 16384.
    // Joiner notifications (op=0B 97 PartyAccept) were among the dropped,
    // surfacing to the user as "내 방 만들고 사람들 들어오면 미터기에 표시
    // 안 됨". 65536 absorbs observed bursts. Memory cost ~4 MB (trivial).
    // Long-term fix: make the dispatcher's synchronous diagnostic log writes
    // non-blocking, so the consumer drains the channel faster.
    public int ChannelCapacity { get; init; } = 65536;
}
