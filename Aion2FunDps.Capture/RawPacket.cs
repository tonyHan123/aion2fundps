using System.Buffers;
using System.Net;

namespace Aion2FunDps.Capture;

public readonly struct RawPacket : IDisposable
{
    public readonly uint SourceIpv4;
    public readonly ushort SourcePort;
    public readonly ushort DestinationPort;
    public readonly uint TcpSequence;
    public readonly long TimestampTicks;
    public readonly byte[] Buffer;
    public readonly int Length;

    public RawPacket(uint sourceIpv4, ushort sourcePort, ushort destinationPort,
        uint tcpSequence, long timestampTicks, byte[] buffer, int length)
    {
        SourceIpv4 = sourceIpv4;
        SourcePort = sourcePort;
        DestinationPort = destinationPort;
        TcpSequence = tcpSequence;
        TimestampTicks = timestampTicks;
        Buffer = buffer;
        Length = length;
    }

    public ReadOnlySpan<byte> Payload => Buffer.AsSpan(0, Length);

    /// <summary>4-tuple flow key for SequenceReorderer / FrameAssembler.</summary>
    public ulong FlowKey => ((ulong)SourceIpv4 << 32) | ((ulong)SourcePort << 16) | DestinationPort;

    public IPAddress GetSourceAddress()
    {
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)(SourceIpv4 >> 24);
        bytes[1] = (byte)(SourceIpv4 >> 16);
        bytes[2] = (byte)(SourceIpv4 >> 8);
        bytes[3] = (byte)SourceIpv4;
        return new IPAddress(bytes);
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);
}
