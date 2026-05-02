using System.Text;
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x33 0x36 — self-character nickname info.
/// Layout: [opcode] [userId varint] [10-byte search for 0x07 spliter]
///         [nameLength varint] [name UTF-8] [server uint16 LE] [job byte]
/// </summary>
public static class SelfNicknameHandler
{
    /// <summary>Last successfully parsed self user_id — exposed so other
    /// handlers (op=0197 lobby broadcast) can filter out lobby-browser
    /// previews of OTHER rooms by checking that this id is in the parsed
    /// member set before treating it as the user's party.</summary>
    public static int? LastSelfUserId { get; private set; }

    /// <summary>Last successfully parsed self nickname. Used as a fallback
    /// when a lobby roster carries entity ids while SELF_NICK carried user id.</summary>
    public static string? LastSelfNickname { get; private set; }

    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out NicknameInfo evt)
    {
        evt = null!;
        if (body.Length < 2) return false;
        if (body[0] != 0x33 || body[1] != 0x36) return false;

        int offset = 2;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int userId, out int n)) return false;
        offset += n;

        // Search next 10 bytes for 0x07 spliter
        if (offset + 10 > body.Length) return false;
        int spliterIdx = -1;
        for (int i = 0; i < 10; i++)
        {
            if (body[offset + i] == 0x07) { spliterIdx = i; break; }
        }
        if (spliterIdx < 0) return false;
        offset += spliterIdx + 1;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int nameLength, out n)) return false;
        offset += n;
        if (nameLength <= 0 || nameLength > 71) return false;
        if (offset + nameLength > body.Length) return false;

        string nickname;
        try
        {
            nickname = Encoding.UTF8.GetString(body.Slice(offset, nameLength));
        }
        catch
        {
            return false;
        }
        if (!IsValidNickname(nickname)) return false;
        offset += nameLength;

        int server = -1;
        int job = -1;
        if (offset + 2 <= body.Length)
        {
            server = body[offset] | (body[offset + 1] << 8);
            offset += 2;
            if (offset < body.Length) job = body[offset];
        }

        // Combat power lives at offset (length - 40) as 4-byte LE — verified across
        // 벅지 (CP=158,526 in 2253-byte packet) and 짭호 (CP=453,168 in 3282-byte
        // packet). The trailing 36 bytes after CP are skill/buff slot template.
        // Sanity-gate the value: real Aion 2 KR CPs are 10k–50M; anything outside
        // means we're reading from a truncated/malformed packet — emit 0 as "unknown".
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

        evt = new NicknameInfo(userId, nickname, IsSelf: true, server, job, combatPower, timestampTicks, sourceIpv4);
        LastSelfUserId = userId;
        LastSelfNickname = nickname;
        return true;
    }

    /// <summary>
    /// Strict nickname validator — matches Aion 2 KR's actual character
    /// constraints (Hangul + ASCII alphanumerics only). Mirrors A2Viewer's
    /// PartyStreamParser regex `^[가-힣a-zA-Z0-9]+$`.
    ///
    /// Why strict matters: OtherNicknameHandler probes 5 candidate offsets
    /// for a length-prefixed UTF-8 nickname. With a loose validator that
    /// permitted symbols, the probe could accept a misaligned 7-byte slice
    /// "!까불" — produced when the real packet structure puts a 0x07 flag
    /// byte at offset 0, the real nameLen 0x21 ('!') at offset 1, and the
    /// real name "까불..." at offset 2. The probe at i=0 reads nameLen=7,
    /// slices bytes 1..7 → "!까불" → loose validator accepts → wrong
    /// nickname registered alongside the correct one (사용자 보고: "한
    /// 사람이 !까불 + 까불지마라밤에찾아간다 두 행으로 뜸"). Strict
    /// validator rejects the leading '!', forcing the probe to advance
    /// to i=1 where the real nameLen byte yields the full nickname.
    /// </summary>
    public static bool IsValidNickname(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length > 30) return false;
        foreach (char c in s)
        {
            bool ok = (c >= '가' && c <= '힣')
                  || (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9');
            if (!ok) return false;
        }
        return true;
    }
}
