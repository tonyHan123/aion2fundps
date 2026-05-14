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
    private const int MaxFrameBytes = 256 * 1024;
    private const int MaxCarryoverBytes = 256 * 1024;
    private const int PruneIntervalFeeds = 4096;
    private static readonly long IdleFlowTimeoutTicks = TimeSpan.FromMinutes(2).Ticks;

    private readonly Dictionary<ulong, Carryover> _carryover = new();
    private long _malformedFrames;
    private long _droppedCarryovers;
    private int _feedsSincePrune;

    public long MalformedFrames => Volatile.Read(ref _malformedFrames);
    public long DroppedCarryovers => Volatile.Read(ref _droppedCarryovers);
    public int FlowCount => _carryover.Count;

    public void Feed(in OrderedChunk chunk, Action<GamePacket> onGamePacket)
    {
        if (++_feedsSincePrune >= PruneIntervalFeeds)
        {
            _feedsSincePrune = 0;
            PruneIdleFlows(chunk.TimestampTicks);
        }

        var flowKey = chunk.FlowKey;

        Carryover prior = _carryover.GetValueOrDefault(flowKey);
        int combinedLen = prior.Length + chunk.Length;
        if (combinedLen > MaxCarryoverBytes)
        {
            DropPrior(flowKey, prior);
            ArrayPool<byte>.Shared.Return(chunk.Buffer);
            Interlocked.Increment(ref _droppedCarryovers);
            return;
        }

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
            if (!TryReadVarInt(span, out int varIntValue, out int varIntBytes))
            {
                if (span.Length >= 5)
                {
                    Interlocked.Increment(ref _malformedFrames);
                    offset = combinedLen;
                }
                break;
            }

            // Aion 2 framing: total bytes to consume = varintValue + varintBytes - 4.
            // The "-4" accounts for a 4-byte protocol artifact the spec includes in the
            // size value but excludes from the slice handed to the processor.
            // Source: TK-open-public StreamAssembler.kt — realLength = value + length - 4.
            int realLength = varIntValue + varIntBytes - 4;
            if (realLength <= varIntBytes || realLength > MaxFrameBytes)
            {
                Interlocked.Increment(ref _malformedFrames);
                offset = combinedLen;
                break;
            }

            if (combinedLen - offset < realLength)
                break; // game packet body incomplete — wait for more bytes

            int bodyLength = realLength - varIntBytes;
            byte[] gpBuf = ArrayPool<byte>.Shared.Rent(bodyLength);
            Array.Copy(combined, offset + varIntBytes, gpBuf, 0, bodyLength);
            onGamePacket(new GamePacket(chunk.SourceIpv4, chunk.TimestampTicks, gpBuf, bodyLength));

            offset += realLength;
        }

        int remaining = combinedLen - offset;
        if (remaining > 0)
        {
            if (remaining > MaxCarryoverBytes)
            {
                Interlocked.Increment(ref _droppedCarryovers);
                ArrayPool<byte>.Shared.Return(combined);
                return;
            }

            byte[] carryBuf = ArrayPool<byte>.Shared.Rent(remaining);
            Array.Copy(combined, offset, carryBuf, 0, remaining);
            _carryover[flowKey] = new Carryover(carryBuf, remaining, chunk.TimestampTicks);
        }

        ArrayPool<byte>.Shared.Return(combined);
    }

    public void Reset(ulong flowKey)
    {
        if (_carryover.TryGetValue(flowKey, out var c) && c.Buffer != null)
            ArrayPool<byte>.Shared.Return(c.Buffer);
        _carryover.Remove(flowKey);
    }

    private void DropPrior(ulong flowKey, Carryover prior)
    {
        if (prior.Buffer != null)
            ArrayPool<byte>.Shared.Return(prior.Buffer);
        _carryover.Remove(flowKey);
    }

    private void PruneIdleFlows(long nowTicks)
    {
        if (_carryover.Count == 0 || nowTicks <= 0) return;

        List<ulong>? stale = null;
        foreach (var (flowKey, carryover) in _carryover)
        {
            if (nowTicks - carryover.LastSeenTicks > IdleFlowTimeoutTicks)
                (stale ??= new List<ulong>()).Add(flowKey);
        }
        if (stale == null) return;

        foreach (var flowKey in stale)
        {
            if (_carryover.TryGetValue(flowKey, out var c) && c.Buffer != null)
                ArrayPool<byte>.Shared.Return(c.Buffer);
            _carryover.Remove(flowKey);
        }
        Interlocked.Add(ref _droppedCarryovers, stale.Count);
    }

    public static bool TryReadVarInt(ReadOnlySpan<byte> span, out int value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;
        int shift = 0;
        for (int i = 0; i < span.Length && i < 5; i++)
        {
            byte b = span[i];
            // Reject 5-byte varints whose top byte's low 4 bits would shift
            // into the sign bit — protocol doesn't use values > Int32.MaxValue,
            // so a positive overflow indicates a corrupt prefix. Without this
            // guard a malformed prefix yields a negative `value` that
            // downstream length math treats as "huge", leading to silent
            // drops or wrong attribution (audit 2026-05-04: B7 medium).
            if (i == 4 && (b & 0xF0) != 0) return false;
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (value < 0) return false;
                bytesRead = i + 1;
                return true;
            }
            shift += 7;
        }
        // Reached the 5-byte cap without finding a terminator → malformed.
        return false;
    }

    private readonly struct Carryover
    {
        public readonly byte[]? Buffer;
        public readonly int Length;
        public readonly long LastSeenTicks;
        public Carryover(byte[] buffer, int length, long lastSeenTicks)
        {
            Buffer = buffer;
            Length = length;
            LastSeenTicks = lastSeenTicks;
        }
    }
}
