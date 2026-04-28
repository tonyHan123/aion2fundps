using System.Buffers;

namespace Aion2FunDps.Protocol;

/// <summary>
/// A single discrete game-protocol packet, after VarInt framing strips the length prefix.
/// First two bytes are the opcode. Buffer is ArrayPool-rented — Dispose returns it.
/// </summary>
public readonly struct GamePacket : IDisposable
{
    public readonly uint SourceIpv4;
    public readonly long TimestampTicks;
    public readonly byte[] Buffer;
    public readonly int Length;

    public GamePacket(uint sourceIpv4, long timestampTicks, byte[] buffer, int length)
    {
        SourceIpv4 = sourceIpv4;
        TimestampTicks = timestampTicks;
        Buffer = buffer;
        Length = length;
    }

    public ReadOnlySpan<byte> Data => Buffer.AsSpan(0, Length);

    public byte OpcodeFirst => Length > 0 ? Buffer[0] : (byte)0;
    public byte OpcodeSecond => Length > 1 ? Buffer[1] : (byte)0;

    public string OpcodeHex => Length >= 2 ? $"0x{Buffer[0]:x2} 0x{Buffer[1]:x2}" : "(short)";

    public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);
}
