using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x00 0x8d — mob remaining HP.
/// Layout (TK-open-public):
///   [opcode 2B] [mobId varint] [unknown varint × 3] [currentHp uint32 LE]
/// </summary>
public static class MobHpHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out MobHpUpdate evt)
    {
        evt = null!;
        if (body.Length < 2) return false;
        if (body[0] != 0x00 || body[1] != 0x8d) return false;

        int offset = 2;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int mobId, out int n)) return false;
        offset += n;

        for (int i = 0; i < 3; i++)
        {
            if (!FrameAssembler.TryReadVarInt(body[offset..], out _, out n)) return false;
            offset += n;
        }

        if (offset + 4 > body.Length) return false;
        uint hp = (uint)(body[offset]
                       | (body[offset + 1] << 8)
                       | (body[offset + 2] << 16)
                       | (body[offset + 3] << 24));

        evt = new MobHpUpdate(mobId, hp, timestampTicks, sourceIpv4);
        return true;
    }
}
