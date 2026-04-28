namespace Aion2FunDps.Capture;

public sealed record CaptureOptions
{
    public required string ServerIp { get; init; }
    public int BufferSizeBytes { get; init; } = 64 * 1024 * 1024;
    public int SnapshotLengthBytes { get; init; } = 65535;
    public int ReadTimeoutMs { get; init; } = 100;
    public int ChannelCapacity { get; init; } = 4096;

    public string BpfFilter => $"src host {ServerIp} and tcp";
}
