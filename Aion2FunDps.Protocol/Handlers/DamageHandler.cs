using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x04 0x38 — main damage packet.
/// Layout (TK-open-public StreamProcessor.kt):
///   [opcode 2B] [targetId varint] [switchVar varint] [flag varint] [actorId varint]
///   [skillCode uint32 LE + 1 separator byte = 5B] [type varint]
///   [specials block, size = 8/10/12/14 from switchVar &amp; 7]
///   [unknown varint] [damage varint] [loop varint]
/// </summary>
public static class DamageHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        bool isDot,
        out DamageEvent evt)
    {
        evt = null!;
        if (body.Length < 2) return false;
        if (body[0] != 0x04 || body[1] != 0x38) return false;

        int offset = 2;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int targetId, out int n)) return false;
        offset += n;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int switchVar, out n)) return false;
        offset += n;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int flag, out n)) return false;
        offset += n;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int actorId, out n)) return false;
        offset += n;

        if (offset + 5 > body.Length) return false;
        uint skillCode = (uint)(body[offset]
                              | (body[offset + 1] << 8)
                              | (body[offset + 2] << 16)
                              | (body[offset + 3] << 24));
        offset += 5;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int type, out n)) return false;
        offset += n;

        // specials block size depends on switchVar's lower 3 bits
        int specialsSize = (switchVar & 7) switch
        {
            4 => 8,
            5 => 12,
            6 => 10,
            7 => 14,
            _ => -1,
        };
        if (specialsSize < 0) return false;
        if (offset + specialsSize > body.Length) return false;

        SpecialDamageFlags specials = (SpecialDamageFlags)body[offset];
        offset += specialsSize;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int unknown, out n)) return false;
        offset += n;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int damage, out n)) return false;
        offset += n;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int loop, out n)) return false;
        offset += n;

        // Validity guards (per TK)
        if (actorId == targetId) return false;
        if (damage >= 10_000_000) return false;
        if (damage < 0) return false;

        evt = new DamageEvent(
            ActorId: actorId,
            TargetId: targetId,
            Damage: damage,
            SkillCode: skillCode,
            Type: type,
            Specials: specials,
            Loop: loop,
            IsDot: isDot,
            TimestampTicks: timestampTicks,
            SourceIpv4: sourceIpv4);
        return true;
    }
}
