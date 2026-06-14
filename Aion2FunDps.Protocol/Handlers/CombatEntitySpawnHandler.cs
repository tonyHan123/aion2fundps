// op=22 92 던전 entity 스폰 패킷에서 "전투 액터 id ↔ 닉/서버/직업"을 추출하는 핸들러 (2026-06-13 둥지 분석).
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses op=0x22 0x92 — the in-dungeon entity SPAWN packet. Unlike
/// <see cref="CombatEntityRemapHandler"/> (op=4136, 성역) which in 둥지 carries
/// only position/HP state (no nickname), op=2292 is the packet that actually
/// bridges the gap: it carries the **combat actor_id** (the varint id that
/// op=0438 damage events use) together with the player's GUID + canonical id +
/// nickname + jobCode.
///
/// 왜 필요한가 (사용자 보고 2026-06-13, frames-dump bridge_find_0b97.py 분석):
/// 둥지에서 같은직업 2명(궁시아·올리브마티니, 둘 다 궁성)의 딜이 0으로 나옴.
/// 신원(canon↔닉)은 op=0297/0b97 이 주지만 그 id 는 로비 id 공간이고, 데미지는
/// 별도의 던전 전투 actor_id(예: 3856/10461)로 들어와 연결이 끊김. job-match
/// 폴백(<see cref="Core"/> TryJobMatchAdoption)은 같은직업 2+ 를 모호하다고 스킵.
/// op=2292 는 그 둘을 한 패킷에 담아 1:1 로 이어줌:
///   actor 3856 ↔ 궁시아(canon 105144) / actor 10461 ↔ 올리브마티니(canon 142066)
///   actor 9829 ↔ 하아나(canon 97205)  ← 3개 캡처 모두 검증.
///
/// <see cref="CombatEntityRemapHandler"/> 와 동일하게 INFO ENRICHMENT 전용 —
/// IsPartyMember=false. NicknameInfo(UserId = 전투 actor_id, Nickname) 를 내보내면
/// 레지스트리가 닉으로 기존 canonical 에 그 actor_id 를 alias + 고아 데미지 병합.
///
/// Layout (KR live, reverse-engineered 2026-06-13 from frames-dump.log):
///   offset 0-1     opcode 22 92
///   offset 4-7     combat actor_id 4 LE   ← op=0438 데미지의 actorId
///   offset 8-9     serverId 2 LE
///   offset 12      GUID length (항상 0x24 = 36, layout-drift 가드)
///   offset 13..    36바이트 ASCII GUID
///   GUID 뒤 +0     canonical id 4 LE (로비 id, 검증용)
///   GUID 뒤 +6     serverId 2 LE
///   GUID 뒤 +8     name length 1
///   GUID 뒤 +9     UTF-8 nickname
///   GUID 뒤 +9+len jobCode 4 LE (1..40)
/// </summary>
public static class CombatEntitySpawnHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out NicknameInfo evt)
    {
        evt = null!;
        if (body.Length < 16) return false;
        if (body[0] != 0x22 || body[1] != 0x92) return false;
        if (body[12] != 0x24) return false;   // GUID length marker — guards layout drift / variant packets

        // 전투 actor_id — op=0438 데미지가 쓰는 바로 그 id.
        int actorId = body[4] | (body[5] << 8) | (body[6] << 16) | (body[7] << 24);
        if (actorId <= 0 || actorId > 100_000_000) return false;

        int segOff = 13 + 36;                  // GUID 뒤 식별 레코드 시작
        if (segOff + 9 > body.Length) return false;

        int nameLen = body[segOff + 8];
        if (nameLen < 1 || nameLen > 50) return false;
        int nickOff = segOff + 9;
        if (nickOff + nameLen > body.Length) return false;

        string nickname;
        try
        {
            nickname = System.Text.Encoding.UTF8.GetString(body.Slice(nickOff, nameLen));
        }
        catch
        {
            return false;
        }
        if (!SelfNicknameHandler.IsValidNickname(nickname)) return false;

        int serverId = 0;
        int candidate = body[segOff + 6] | (body[segOff + 7] << 8);
        if (Aion2FunDps.Core.Models.ServerMap.IsKnownServerId(candidate))
            serverId = candidate;

        int job = 0;
        int jobOff = nickOff + nameLen;
        if (jobOff + 4 <= body.Length)
        {
            int jobRaw = body[jobOff] | (body[jobOff + 1] << 8) | (body[jobOff + 2] << 16) | (body[jobOff + 3] << 24);
            if (jobRaw >= 1 && jobRaw <= 40) job = jobRaw;
        }

        // UserId = 전투 actor_id. 레지스트리가 닉으로 기존 canonical 에 alias.
        // CombatPower 는 0 — 레지스트리의 no-downgrade 병합이 op=0297/0092 값 유지.
        evt = new NicknameInfo(
            UserId: actorId,
            Nickname: nickname,
            IsSelf: false,
            Server: serverId,
            Job: job,
            CombatPower: 0,
            TimestampTicks: timestampTicks,
            SourceIpv4: sourceIpv4,
            IsPartyMember: false);
        return true;
    }
}
