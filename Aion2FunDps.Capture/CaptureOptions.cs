namespace Aion2FunDps.Capture;

public sealed record CaptureOptions
{
    /// <summary>
    /// Any IP in the game server's subnet — used only for OS routing probe to identify the correct NIC.
    /// Does not need to be reachable; OS uses routing table to determine egress interface.
    /// </summary>
    public string RoutingProbeIp { get; init; } = "206.127.156.1";

    /// <summary>
    /// BPF filter expression. Default captures NCSoft KR Aion 2 /24 subnet.
    /// NCSoft load-balances each session to different IPs in this block, so we capture the whole subnet.
    /// </summary>
    public string BpfFilter { get; init; } = "src net 206.127.156.0/24 and tcp";

    public int BufferSizeBytes { get; init; } = 64 * 1024 * 1024;
    public int SnapshotLengthBytes { get; init; } = 65535;
    public int ReadTimeoutMs { get; init; } = 100;
    public int ChannelCapacity { get; init; } = 4096;
}
