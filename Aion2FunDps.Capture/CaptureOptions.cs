namespace Aion2FunDps.Capture;

public sealed record CaptureOptions
{
    /// <summary>
    /// Any IP in the game server's subnet. Used only for the OS routing probe
    /// to identify the correct NIC; it does not need to be reachable.
    /// </summary>
    public string RoutingProbeIp { get; init; } = "206.127.156.1";

    /// <summary>
    /// BPF filter expression. Keep this narrow: every packet admitted here is
    /// copied into managed memory and run through TCP reorder / frame assembly
    /// before the dispatcher can reject non-game traffic.
    ///
    /// The old discovery filter ("tcp or udp") was useful for reverse
    /// engineering lobby packets, but it also pulled browser, Discord, and OS
    /// traffic through Release builds. That can contend with the game for CPU
    /// and memory bandwidth and show up as frame drops.
    ///
    /// Known server ranges:
    ///   206.127.x.x - game-zone traffic
    ///   216.107.x.x - lobby/matchmaking candidates observed in captures
    /// UDP is excluded because the app does not parse UDP gameplay frames.
    /// </summary>
    public string BpfFilter { get; init; } =
        "tcp and (src net 206.127.0.0/16 or src net 216.107.0.0/16)";

    public int BufferSizeBytes { get; init; } = 64 * 1024 * 1024;
    public int SnapshotLengthBytes { get; init; } = 65535;
    public int ReadTimeoutMs { get; init; } = 100;

    // 65536 absorbs observed lobby bursts. Memory cost is about 4 MB. If this
    // ever saturates in Release again, first suspect a too-wide BPF filter.
    public int ChannelCapacity { get; init; } = 65536;
}
