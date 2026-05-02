using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x40 0x36 — entity spawn (mobs, NPCs, summons, bosses).
///
/// Two captured wire variants (KR live, 2026-04 빌드):
///
/// Variant A (no name) — emitted for mobs/NPCs without an owner:
///   offset 0-1     opcode 40 36
///   offset 2..     VarInt entityId
///   +1             type byte (0x0c / 0x04 / 0x22 observed)
///   +2             0x__ 0x00       (size_low, mode_flag = 0x00)
///   +4             mob_code (4-byte LE)
///   ... payload
///
/// Variant B (named, owner-attributed) — emitted for summons/pets:
///   offset 0-1     opcode 40 36
///   offset 2..     VarInt entityId
///   +1             type byte (0x5f and others observed)
///   +2             0x00 0x01       (size_low, mode_flag = 0x01 → name follows)
///   +4             name length (1 byte)
///   +5..+5+L       UTF-8 owner name (e.g., "메인딜러입니다")
///   +5+L..+8+L     mob_code (4-byte LE)
///   ... payload
///
/// We discriminate by inspecting the byte at (2 + varintLen + 2): 0x00 = no-name,
/// 0x01 = named. The owner_name in variant B is the player nickname that owns
/// this entity — the aggregator looks it up in NicknameRegistry to wire summon→player.
/// </summary>
public static class SummonSpawnHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out SummonSpawnInfo evt)
    {
        evt = null!;
        if (body.Length < 12) return false;
        if (body[0] != 0x40 || body[1] != 0x36) return false;

        if (!FrameAssembler.TryReadVarInt(body[2..], out int entityId, out int varintLen))
            return false;

        int afterVarInt = 2 + varintLen;
        // Layout after VarInt: [type_byte] [size_low] [mode_flag] ...
        if (afterVarInt + 3 > body.Length) return false;
        byte modeFlag = body[afterVarInt + 2];

        int mobCodeOffset;
        string? ownerName = null;

        if (modeFlag == 0x00)
        {
            // Variant A: mob_code at +3
            mobCodeOffset = afterVarInt + 3;
        }
        else if (modeFlag == 0x01)
        {
            // Variant B: name length at +3, name at +4..+3+L, mob_code at +4+L
            int nameLenOffset = afterVarInt + 3;
            if (nameLenOffset >= body.Length) return false;
            int nameLen = body[nameLenOffset];
            int nameOffset = nameLenOffset + 1;
            if (nameOffset + nameLen > body.Length) return false;

            try
            {
                ownerName = System.Text.Encoding.UTF8.GetString(body.Slice(nameOffset, nameLen));
            }
            catch { ownerName = null; }

            mobCodeOffset = nameOffset + nameLen;
        }
        else
        {
            return false;
        }

        if (mobCodeOffset + 4 > body.Length) return false;

        int mobCode = body[mobCodeOffset]
                    | (body[mobCodeOffset + 1] << 8)
                    | (body[mobCodeOffset + 2] << 16)
                    | (body[mobCodeOffset + 3] << 24);

        if (mobCode < 1_000_000 || mobCode > 50_000_000) return false;

        evt = new SummonSpawnInfo(
            SummonId: entityId,
            OwnerId: 0,
            MobCode: mobCode,
            TimestampTicks: timestampTicks,
            SourceIpv4: sourceIpv4,
            OwnerName: ownerName);
        return true;
    }
}
