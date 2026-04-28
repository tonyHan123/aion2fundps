using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x05 0x38 — DOT (damage over time) tick.
/// Layout (TK parseDoTPacket):
///   [opcode] [targetId varint] [bitFlag byte, must have 0x02 set]
///   [actorId varint, != target] [unknown varint]
///   [skillCodeCandidate uint32 LE, /10 or /100 lookup] [damage varint]
/// </summary>
public static class DotHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out DamageEvent evt)
    {
        evt = null!;
        if (body.Length < 2) return false;
        if (body[0] != 0x05 || body[1] != 0x38) return false;

        int offset = 2;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int targetId, out int n)) return false;
        offset += n;
        if (offset >= body.Length) return false;

        // Bit flag byte — must have 0x02 set, else this is not a real DOT we count
        byte flagByte = body[offset];
        if ((flagByte & 0x02) == 0) return false;
        offset++;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int actorId, out n)) return false;
        if (actorId == targetId) return false;
        offset += n;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out _, out n)) return false; // unknown
        offset += n;

        if (offset + 4 > body.Length) return false;
        uint skillCodeCandidate = (uint)(body[offset]
                                       | (body[offset + 1] << 8)
                                       | (body[offset + 2] << 16)
                                       | (body[offset + 3] << 24));
        // TK divides by 10 by default (resolution to /100 happens in display layer)
        uint skillCode = skillCodeCandidate / 10;
        offset += 4;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int damage, out _)) return false;
        if (damage < 0 || damage >= 10_000_000) return false;

        evt = new DamageEvent(
            ActorId: actorId,
            TargetId: targetId,
            Damage: damage,
            SkillCode: skillCode,
            Type: 0,
            Specials: SpecialDamageFlags.None,
            Loop: 0,
            IsDot: true,
            TimestampTicks: timestampTicks,
            SourceIpv4: sourceIpv4);
        return true;
    }
}
