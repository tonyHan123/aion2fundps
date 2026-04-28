using System.Buffers;
using Aion2FunDps.Capture;

namespace Aion2FunDps.Protocol;

/// <summary>
/// Slices the in-order TCP byte stream into discrete game packets via VarInt length prefix.
/// Maintains per-flow (4-tuple) carryover bytes for game packets that span multiple TCP chunks.
///
/// VarInt format (Google protobuf style):
///   Each byte's bit 7 = continuation flag; bits 0-6 = 7 bits of value (little-endian).
///   1-5 bytes for 32-bit value.
///
/// IMPORTANT: Feed() takes ownership of the OrderedChunk's buffer.
/// </summary>
public sealed class FrameAssembler
{
    private readonly Dictionary<ulong, Carryover> _carryover = new();
    private long _malformedFrames;

    public long MalformedFrames => Volatile.Read(ref _malformedFrames);
    public int FlowCount => _carryover.Count;

    public void Feed(in OrderedChunk chunk, Action<GamePacket> onGamePacket)
    {
        var flowKey = chunk.FlowKey;

        Carryover prior = _carryover.GetValueOrDefault(flowKey);
        int combinedLen = prior.Length + chunk.Length;
        byte[] combined = ArrayPool<byte>.Shared.Rent(combinedLen);

        if (prior.Length > 0)
        {
            Array.Copy(prior.Buffer!, 0, combined, 0, prior.Length);
            ArrayPool<byte>.Shared.Return(prior.Buffer!);
            _carryover.Remove(flowKey);
        }
        Array.Copy(chunk.Buffer, 0, combined, prior.Length, chunk.Length);
        ArrayPool<byte>.Shared.Return(chunk.Buffer);

        int offset = 0;
        while (offset < combinedLen)
        {
            var span = combined.AsSpan(offset, combinedLen - offset);
            if (!TryReadVarInt(span, out int packetBodyLen, out int varIntBytes))
                break;

            if (packetBodyLen <= 0)
            {
                Interlocked.Increment(ref _malformedFrames);
                offset = combinedLen;
                break;
            }

            int totalRequired = varIntBytes + packetBodyLen;
            if (combinedLen - offset < totalRequired)
                break;

            byte[] gpBuf = ArrayPool<byte>.Shared.Rent(packetBodyLen);
            Array.Copy(combined, offset + varIntBytes, gpBuf, 0, packetBodyLen);
            onGamePacket(new GamePacket(chunk.SourceIpv4, chunk.TimestampTicks, gpBuf, packetBodyLen));

            offset += totalRequired;
        }

        int remaining = combinedLen - offset;
        if (remaining > 0)
        {
            byte[] carryBuf = ArrayPool<byte>.Shared.Rent(remaining);
            Array.Copy(combined, offset, carryBuf, 0, remaining);
            _carryover[flowKey] = new Carryover(carryBuf, remaining);
        }

        ArrayPool<byte>.Shared.Return(combined);
    }

    public void Reset(ulong flowKey)
    {
        if (_carryover.TryGetValue(flowKey, out var c) && c.Buffer != null)
            ArrayPool<byte>.Shared.Return(c.Buffer);
        _carryover.Remove(flowKey);
    }

    public static bool TryReadVarInt(ReadOnlySpan<byte> span, out int value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;
        int shift = 0;
        for (int i = 0; i < span.Length && i < 5; i++)
        {
            byte b = span[i];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                bytesRead = i + 1;
                return true;
            }
            shift += 7;
        }
        return false;
    }

    private readonly struct Carryover
    {
        public readonly byte[]? Buffer;
        public readonly int Length;
        public Carryover(byte[] buffer, int length) { Buffer = buffer; Length = length; }
    }
}
