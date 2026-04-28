using System.Buffers;

namespace Aion2FunDps.Capture;

/// <summary>
/// A contiguous chunk of TCP-stream bytes from a single 4-tuple flow, in correct sequence order.
/// Buffer is ArrayPool-rented — Dispose returns it. Single-consumer ownership.
/// </summary>
public readonly struct OrderedChunk : IDisposable
{
    public readonly uint SourceIpv4;
    public readonly ushort SourcePort;
    public readonly ushort DestinationPort;
    public readonly uint StartSeq;
    public readonly long TimestampTicks;
    public readonly byte[] Buffer;
    public readonly int Length;

    public OrderedChunk(uint sourceIpv4, ushort sourcePort, ushort destinationPort,
        uint startSeq, long timestampTicks, byte[] buffer, int length)
    {
        SourceIpv4 = sourceIpv4;
        SourcePort = sourcePort;
        DestinationPort = destinationPort;
        StartSeq = startSeq;
        TimestampTicks = timestampTicks;
        Buffer = buffer;
        Length = length;
    }

    public ReadOnlySpan<byte> Data => Buffer.AsSpan(0, Length);

    public ulong FlowKey => ((ulong)SourceIpv4 << 32) | ((ulong)SourcePort << 16) | DestinationPort;

    public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);
}
