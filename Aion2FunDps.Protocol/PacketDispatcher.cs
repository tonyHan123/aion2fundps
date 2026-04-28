using Aion2FunDps.Core;
using Aion2FunDps.Protocol.Handlers;

namespace Aion2FunDps.Protocol;

/// <summary>
/// Routes GamePackets to opcode-specific handlers, emits typed IGameEvents.
/// Handles LZ4-compressed packets recursively (decompress → re-frame inner packets).
/// </summary>
public sealed class PacketDispatcher
{
    private readonly Lz4Decompressor _lz4 = new();

    private long _knownCount;
    private long _unknownCount;
    private long _malformedCount;

    public Lz4Decompressor Lz4 => _lz4;
    public long KnownCount => Volatile.Read(ref _knownCount);
    public long UnknownCount => Volatile.Read(ref _unknownCount);
    public long MalformedCount => Volatile.Read(ref _malformedCount);

    public void Dispatch(in GamePacket gp, Action<IGameEvent> emit)
    {
        var data = gp.Data;
        if (CompressionDetector.IsCompressed(data, out int compOffset))
        {
            DispatchCompressed(data[compOffset..], gp.SourceIpv4, gp.TimestampTicks, emit);
            return;
        }

        DispatchOpcode(data, gp.SourceIpv4, gp.TimestampTicks, emit);
    }

    private void DispatchCompressed(ReadOnlySpan<byte> compressed, uint sourceIpv4, long ticks, Action<IGameEvent> emit)
    {
        var compressedArray = compressed.ToArray();
        _lz4.TryDecompress(compressedArray, decompressed =>
        {
            var span = decompressed.Span;
            int offset = 0;
            while (offset < span.Length)
            {
                if (!FrameAssembler.TryReadVarInt(span[offset..], out int v, out int vBytes))
                    break;
                int realLen = v + vBytes - 4;
                if (realLen <= vBytes || span.Length - offset < realLen)
                    break;

                var innerBody = span.Slice(offset + vBytes, realLen - vBytes);
                DispatchOpcode(innerBody, sourceIpv4, ticks, emit);
                offset += realLen;
            }
        });
    }

    private void DispatchOpcode(ReadOnlySpan<byte> body, uint sourceIpv4, long ticks, Action<IGameEvent> emit)
    {
        if (body.Length < 2)
        {
            Interlocked.Increment(ref _malformedCount);
            return;
        }

        byte op0 = body[0];
        byte op1 = body[1];

        // 0x04 0x38 = DAMAGE
        if (op0 == 0x04 && op1 == 0x38)
        {
            if (DamageHandler.TryParse(body, ticks, sourceIpv4, isDot: false, out var dmg))
            {
                emit(dmg);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x00 0x8d = MOB_HP
        if (op0 == 0x00 && op1 == 0x8d)
        {
            if (MobHpHandler.TryParse(body, ticks, sourceIpv4, out var hp))
            {
                emit(hp);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x33 0x36 = SELF_NICK
        if (op0 == 0x33 && op1 == 0x36)
        {
            if (SelfNicknameHandler.TryParse(body, ticks, sourceIpv4, out var nick))
            {
                emit(nick);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x21 0x8d = COMBAT_BOUNDARY
        if (op0 == 0x21 && op1 == 0x8d)
        {
            if (CombatBoundaryHandler.TryParse(body, ticks, sourceIpv4, out var cb))
            {
                emit(cb);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // Unhandled (yet) — DOT, OTHER_NICK, SUMMON, BUFF, etc.
        Interlocked.Increment(ref _unknownCount);
    }
}
