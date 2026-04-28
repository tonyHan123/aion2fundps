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
    private long _selfNickSeen;
    private long _selfNickParsed;
    private long _otherNickSeen;
    private long _otherNickParsed;

    public Lz4Decompressor Lz4 => _lz4;
    public long KnownCount => Volatile.Read(ref _knownCount);
    public long UnknownCount => Volatile.Read(ref _unknownCount);
    public long MalformedCount => Volatile.Read(ref _malformedCount);
    public long SelfNickSeen => Volatile.Read(ref _selfNickSeen);
    public long SelfNickParsed => Volatile.Read(ref _selfNickParsed);
    public long OtherNickSeen => Volatile.Read(ref _otherNickSeen);
    public long OtherNickParsed => Volatile.Read(ref _otherNickParsed);

    // Diagnostic: log raw bytes of failed SELF_NICK / OTHER_NICK to file for offline analysis
    public string? DiagnosticLogPath { get; set; }
    private readonly object _logLock = new();

    private void LogPacket(string opcodeName, ReadOnlySpan<byte> body, bool parsed)
    {
        if (DiagnosticLogPath == null) return;
        try
        {
            var hex = string.Join(" ", body[..Math.Min(64, body.Length)].ToArray().Select(b => b.ToString("x2")));
            lock (_logLock)
            {
                System.IO.File.AppendAllText(DiagnosticLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} {opcodeName} parsed={parsed} len={body.Length}: {hex}\n");
            }
        }
        catch { }
    }

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

    private void DispatchCompressed(ReadOnlySpan<byte> compressedSection, uint sourceIpv4, long ticks, Action<IGameEvent> emit)
    {
        // Aion 2 layout after 0xff 0xff marker:
        //   [originLength: uint32 LE, 4 bytes] [LZ4 compressed data]
        if (compressedSection.Length < 5) return;

        int originLength = compressedSection[0]
                         | (compressedSection[1] << 8)
                         | (compressedSection[2] << 16)
                         | (compressedSection[3] << 24);

        var compressed = compressedSection[4..].ToArray();

        _lz4.TryDecompress(compressed, originLength, decompressed =>
        {
            var span = decompressed.Span;
            int offset = 0;
            while (offset < span.Length)
            {
                if (!FrameAssembler.TryReadVarInt(span[offset..], out int v, out int vBytes))
                    break;

                // varint == 0 means skip 1 byte (per TK StreamProcessor)
                if (v == 0)
                {
                    offset += 1;
                    continue;
                }

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
            Interlocked.Increment(ref _selfNickSeen);
            bool ok = SelfNicknameHandler.TryParse(body, ticks, sourceIpv4, out var nick);
            LogPacket("SELF_NICK", body, ok);
            if (ok)
            {
                Interlocked.Increment(ref _selfNickParsed);
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

        // 0x05 0x38 = DOT (damage over time)
        if (op0 == 0x05 && op1 == 0x38)
        {
            if (DotHandler.TryParse(body, ticks, sourceIpv4, out var dot))
            {
                emit(dot);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x44 0x36 = OTHER_NICK
        if (op0 == 0x44 && op1 == 0x36)
        {
            Interlocked.Increment(ref _otherNickSeen);
            bool ok = OtherNicknameHandler.TryParse(body, ticks, sourceIpv4, out var nick);
            LogPacket("OTHER_NICK", body, ok);
            if (ok)
            {
                Interlocked.Increment(ref _otherNickParsed);
                emit(nick);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // 0x40 0x36 = SUMMON_SPAWN
        if (op0 == 0x40 && op1 == 0x36)
        {
            if (SummonSpawnHandler.TryParse(body, ticks, sourceIpv4, out var sm))
            {
                emit(sm);
                Interlocked.Increment(ref _knownCount);
            }
            else
            {
                Interlocked.Increment(ref _malformedCount);
            }
            return;
        }

        // Unhandled (yet) — BUFF (0x2a/0x2b 0x38), party events (0x__ 0x97), etc.
        Interlocked.Increment(ref _unknownCount);
    }
}
