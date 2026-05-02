using System.Text;
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x44 0x36 — other-player nickname info. Auto-detects party members
/// and nearby players without requiring user to open character info window.
///
/// Layout (TK searchOtherNickname):
///   [opcode] [userId varint] [unknown1 varint] [unknown2 varint] [skip 1 byte]
///   [search +0..+4 for valid nickname varint length 1-71] [nickname UTF-8]
///   [job byte] [search for valid server uint16 LE in 1001..1021 or 2001..2021]
///   [legion name varint length 2-24] [legion name]
/// </summary>
public static class OtherNicknameHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out NicknameInfo evt)
    {
        evt = null!;
        if (body.Length < 2) return false;
        if (body[0] != 0x44 || body[1] != 0x36) return false;

        int offset = 2;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int userId, out int n)) return false;
        offset += n;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out _, out n)) return false; // unknown1
        offset += n;
        if (!FrameAssembler.TryReadVarInt(body[offset..], out _, out n)) return false; // unknown2
        offset += n;

        if (body.Length - offset <= 2) return false;
        offset += 1; // skip 1 byte (per TK)

        // Brute-force search offset 0..4 for valid nickname varint
        int baseOffset = offset;
        string? nickname = null;
        int nickEnd = -1;
        for (int i = 0; i < 5; i++)
        {
            int probe = baseOffset + i;
            if (probe >= body.Length) continue;
            if (!FrameAssembler.TryReadVarInt(body[probe..], out int nameLen, out int nameLenBytes)) continue;
            if (nameLen < 1 || nameLen > 71) continue;
            int nameStart = probe + nameLenBytes;
            if (nameStart + nameLen > body.Length) continue;

            string candidate;
            try { candidate = Encoding.UTF8.GetString(body.Slice(nameStart, nameLen)); }
            catch { continue; }

            if (!SelfNicknameHandler.IsValidNickname(candidate)) continue;
            nickname = candidate;
            nickEnd = nameStart + nameLen;
            break;
        }
        if (nickname == null || nickEnd < 0) return false;

        offset = nickEnd;
        if (offset >= body.Length) return false;
        int job = body[offset];
        offset += 1;

        // Brute-force search for valid server (1001-1021 or 2001-2021)
        int server = -1;
        int serverBase = offset;
        for (int i = 0; i < 32; i++) // bound the search
        {
            int probe = serverBase + i;
            if (probe + 2 > body.Length) break;
            int candidate = body[probe] | (body[probe + 1] << 8);
            bool inRange = (candidate >= 1001 && candidate <= 1021) ||
                           (candidate >= 2001 && candidate <= 2021);
            if (!inRange) continue;
            server = candidate;
            break;
        }

        // Same CP-at-tail-40 rule as SELF_NICK — Aion 2's nickname packets share
        // a common stat block at the end. Sanity-gate to filter out truncated
        // packets that would otherwise emit garbage values.
        int combatPower = 0;
        if (body.Length >= 50)
        {
            int cpOffset = body.Length - 40;
            int cpRaw = body[cpOffset]
                      | (body[cpOffset + 1] << 8)
                      | (body[cpOffset + 2] << 16)
                      | (body[cpOffset + 3] << 24);
            if (cpRaw >= 10_000 && cpRaw <= 999_999)
                combatPower = cpRaw;
        }

        evt = new NicknameInfo(userId, nickname, IsSelf: false, server, job, combatPower, timestampTicks, sourceIpv4);
        return true;
    }
}
