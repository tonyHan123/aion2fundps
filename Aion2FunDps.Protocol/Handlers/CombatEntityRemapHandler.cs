// op=41 36 던전 내 entity_id ↔ 닉네임 매핑을 추출하는 핸들러 (2026-06-13 성역 분석).
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses op=0x41 0x36 — the in-dungeon entity/state broadcast that carries a
/// player's nickname together with their dungeon-space entity_id. This is the
/// channel that recovers identity when the game-update broke the old
/// actor↔nick mapping (2026-06-12): mid-dungeon, the only authoritative
/// nick source was op=0297 (lobby id space), so a member whose damage arrived
/// under a dungeon entity_id stayed unresolved — and the job-match fallback
/// (<see cref="Core"/> TryJobMatchAdoption) skips any class with 2+ unassigned
/// members. 성역 has two healers (뇌성/옹팡, jobCode 30) routinely hitting that
/// skip, so their 61만/283만 딜 piled onto anonymous "치유성N" rows.
///
/// Like <see cref="PartyMemberStatusHandler"/> this is INFO ENRICHMENT only —
/// IsPartyMember=false. It emits a NicknameInfo(entityId → nickname); the
/// registry aliases that dungeon entity_id onto the existing canonical (matched
/// by nickname) and merges the orphan damage in (RegisterCanonical).
///
/// Layout (KR live, reverse-engineered 2026-06-13 from frames-dump.log; see
/// tools/reverse_4136.py + raw_context_4136.py):
///   offset 0-1     opcode 41 36
///   offset 2-4     record header id (3-byte, varies per packet — NOT stable, ignore)
///   offset 7       0x01 record-1 marker
///   offset 8       name length (1 byte)
///   offset 9..     UTF-8 nickname
///   later          dungeon entity_id, framed by the fixed signature
///                  07 02 06 [entityId 4 LE] 3d 0a 00 00
///                  (trailing 3d 0a 00 00 00 00 held in 65/65 captured samples).
///
/// Safety: the entity_id is read ONLY from that signature, and the registry's
/// nickname-keyed merge means a misframed id can't attach to the wrong member —
/// validated against the 02:34 캡처 where raw never appeared under a foreign
/// nickname (reverse_4136.py: raw-in-other-eid = 0).
/// </summary>
public static class CombatEntityRemapHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out NicknameInfo evt)
    {
        evt = null!;
        if (body.Length < 16) return false;
        if (body[0] != 0x41 || body[1] != 0x36) return false;
        if (body[7] != 0x01) return false;   // record-1 marker, guards against layout drift

        int nameLen = body[8];
        if (nameLen < 1 || nameLen > 50) return false;
        if (9 + nameLen > body.Length) return false;

        string nickname;
        try
        {
            nickname = System.Text.Encoding.UTF8.GetString(body.Slice(9, nameLen));
        }
        catch
        {
            return false;
        }
        if (!SelfNicknameHandler.IsValidNickname(nickname)) return false;

        int entityId = FindEntityId(body);
        if (entityId <= 0) return false;

        // Server/job/CP intentionally left 0 — the registry's no-downgrade merge
        // keeps whatever op=0297 already established. This packet's job is the
        // entityId↔nickname bridge, nothing else.
        evt = new NicknameInfo(
            UserId: entityId,
            Nickname: nickname,
            IsSelf: false,
            Server: 0,
            Job: 0,
            CombatPower: 0,
            TimestampTicks: timestampTicks,
            SourceIpv4: sourceIpv4,
            IsPartyMember: false);
        return true;
    }

    /// <summary>
    /// Dungeon entity_id sits inside the fixed signature 07 02 06 [id4] 3d 0a 00 00.
    /// Returns 0 when the signature is absent (nick-only state update — common;
    /// ~half of op=4136 packets carry no entity record).
    /// </summary>
    private static int FindEntityId(ReadOnlySpan<byte> b)
    {
        for (int i = 0; i + 11 <= b.Length; i++)
        {
            if (b[i] == 0x07 && b[i + 1] == 0x02 && b[i + 2] == 0x06
                && b[i + 7] == 0x3d && b[i + 8] == 0x0a && b[i + 9] == 0x00 && b[i + 10] == 0x00)
            {
                int id = b[i + 3] | (b[i + 4] << 8) | (b[i + 5] << 16) | (b[i + 6] << 24);
                if (id > 0 && id < 100_000_000) return id;
            }
        }
        return 0;
    }
}
