using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses the 0x__ 0x97 / 0x__ 0xe2 family for INFO ENRICHMENT only — never
/// emits IsPartyMember=true. Membership tracking flows exclusively through:
///   op=01 97 / 02 97  → PartyAssemblyHandler (authoritative roster)
///   op=1D 97          → PartyLeft (user left / room dissolved)
///   op=00 92          → CombatPowerHandler (CP refresh, no roster change)
///
/// This handler still fires for every nearby player / friend status / live
/// pings the game broadcasts, but its only job is to keep
/// <see cref="NicknameRegistry"/> fresh — entityId → (nickname, server, job,
/// CP) mapping that powers damage attribution and class-icon rendering when
/// damage events later target that entityId. Without this enrichment, distant
/// party members in dungeons (outside OTHER_NICK's visibility cone) would
/// show up as "Actor_NNNN" once they dealt damage.
///
/// Captured layout (KR live, May 2026):
///   offset 0-1     opcode (78 e2 / 0b 97 / 1f 97 / etc.)
///   offset 2-3     sub-opcode
///   offset 4-7     entityId 4-byte LE
///   offset 8-9     padding (00 00)
///   offset 10-11   serverId 2-byte LE (matches <see cref="Core.Models.ServerMap"/>)
///   offset 12      name length (1 byte)
///   offset 13..    UTF-8 nickname
///   statsOffset+0  jobCode 4 LE (1..40, mapped via JobClassDetector.FromGameJobCode)
///   statsOffset+12 trailer flag (0x01 for full LiveStatus, else CP not present)
///   statsOffset+18 CP 4 LE (only valid when trailer flag = 0x01)
///
/// Excluded opcodes (control / non-member, per A2Viewer May 2026 RE):
///   02 97 (party update — PartyAssemblyHandler), 6a e2 (friend list bulk),
///   07 97 (party request — applicant not yet a member),
///   13 97 / 2A 97 (board refresh control), 1D 97 (party leave),
///   04 97 (dungeon exit).
/// </summary>
public static class PartyMemberStatusHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out NicknameInfo evt)
    {
        evt = null!;
        if (body.Length < 16) return false;

        byte op0 = body[0], op1 = body[1];

        // 0x__ 0xe2 family: status pings.
        // 0x__ 0x97 family: variants.
        //
        // Excluded opcodes per A2Viewer's PartyStreamParser (May 2026 RE):
        //   0x02 0x97 — party UPDATE (handled by PartyAssemblyHandler)
        //   0x6a 0xe2 — bulk friend list / zone population (not party)
        //   0x07 0x97 — party REQUEST. Carries the applicant's nickname + CP
        //               + class + level but they are NOT a member yet (private
        //               room: just sent a join request). A2Viewer routes this
        //               to a separate PartyRequest event, not the leaderboard.
        //               Without this exclusion, every join request to the
        //               user's room surfaced as a phantom row (사용자 보고:
        //               "비공개 방 만들고 입장 요청만 와도 닉이 뜸").
        //   0x13 0x97 / 0x2A 0x97 — board-refresh control packets, no member info.
        //   0x1D 0x97 — party-leave signal, handled by PartyLeft path in dispatcher.
        //   0x04 0x97 — dungeon-exit signal, no member info.
        bool isHandled =
            (op1 == 0xe2 && op0 != 0x6a) ||
            (op1 == 0x97 && op0 != 0x02
                          && op0 != 0x07
                          && op0 != 0x13
                          && op0 != 0x1d
                          && op0 != 0x2a
                          && op0 != 0x04);
        if (!isHandled) return false;

        // entityId at offset 4-7 (4-byte LE)
        int entityId = body[4]
                     | (body[5] << 8)
                     | (body[6] << 16)
                     | (body[7] << 24);
        if (entityId <= 0 || entityId > 100_000_000) return false;

        // name_len at offset 12. Plausible nickname length is 1..50 chars; UTF-8
        // Korean is 3 bytes/char, so byte-length <= 50 is a generous gate.
        int nameLen = body[12];
        if (nameLen < 1 || nameLen > 50) return false;
        if (13 + nameLen > body.Length) return false;

        string nickname;
        try
        {
            nickname = System.Text.Encoding.UTF8.GetString(body.Slice(13, nameLen));
        }
        catch
        {
            return false;
        }

        // Strict validator (Hangul + ASCII alphanumerics only) — same
        // rule used by SelfNicknameHandler. Rejects misaligned slices
        // that begin with a flag byte like 0x21 ('!'), which would
        // otherwise produce phantom "!까불" entries alongside the real
        // nickname.
        if (!SelfNicknameHandler.IsValidNickname(nickname)) return false;

        // server id at offset 10-11 (LE uint16). Without this, host-room
        // joiners arriving via LiveStatus had Server=0 and the UI couldn't
        // render the "[server]" suffix until a much-later op=0297 broadcast
        // included them — making cross-server members look same-server until
        // the lobby sync caught up.
        int serverId = 0;
        if (body.Length >= 12)
        {
            int candidate = body[10] | (body[11] << 8);
            if (Aion2FunDps.Core.Models.ServerMap.IsKnownServerId(candidate))
                serverId = candidate;
        }

        // CP at statsOffset+18 in full LiveStatus packets, gated on the
        // 0x01 trailer flag at statsOffset+12. Friend-online pings (op=78e2)
        // and similar lightweight broadcasts skip the stats trailer, so they
        // reach this point with combatPower=0 — that's fine, the registry
        // just preserves whatever it already had via cross-id name lookup.
        int statsOffset = 13 + nameLen;
        bool hasLiveStatusTrailer =
            statsOffset + 13 <= body.Length
            && body[statsOffset + 12] == 0x01;

        int combatPower = 0;
        if (hasLiveStatusTrailer)
        {
            int liveCpOffset = statsOffset + 18;
            if (liveCpOffset + 4 <= body.Length)
            {
                int cpRaw = body[liveCpOffset]
                          | (body[liveCpOffset + 1] << 8)
                          | (body[liveCpOffset + 2] << 16)
                          | (body[liveCpOffset + 3] << 24);
                if (cpRaw >= 10_000 && cpRaw <= 999_999) combatPower = cpRaw;
            }
        }

        // jobCode at statsOffset+0 (4-byte LE), plausibility 1..40 (matches
        // A2Viewer's IsPlausibleJobCode). Drives the class icon — registered
        // immediately so icons render before the entity has dealt damage.
        int job = 0;
        if (statsOffset + 4 <= body.Length)
        {
            int jobRaw = body[statsOffset]
                       | (body[statsOffset + 1] << 8)
                       | (body[statsOffset + 2] << 16)
                       | (body[statsOffset + 3] << 24);
            if (jobRaw >= 1 && jobRaw <= 40) job = jobRaw;
        }

        // op=0B 97 = PartyAccept (A2Viewer's case 11). This is the
        // "someone just joined the room" signal. Verified from log
        // 2026-05-02 01:30:15 — when joiner 플레이이데스 accepted the
        // user's private-room invite, this opcode fired with full
        // member info (entityId, server, CP=475k, jobCode) 11 seconds
        // before the next op=0197 Weak roster update. Emitting
        // IsPartyMember=true here closes a 17-second gap during which
        // the leaderboard didn't show the new member.
        //
        // Other 0x__ 0x97 / 0x__ 0xe2 opcodes stay IsPartyMember=false:
        // they're status pings / friend updates / lobby noise that
        // shouldn't add rows on their own. Real membership otherwise
        // flows through PartyAssemblyHandler (01 97 / 02 97) and
        // PartyLeft (1D 97). Status-ping 0B 97 packets that fire for
        // already-joined members are idempotent — RoomLifecycleTracker.
        // AddLiveMember is a no-op when the entityId is already in the
        // roster.
        bool isPartyAccept = op0 == 0x0B && op1 == 0x97;

        evt = new NicknameInfo(
            UserId: entityId,
            Nickname: nickname,
            IsSelf: false,
            Server: serverId,
            Job: job,
            CombatPower: combatPower,
            TimestampTicks: timestampTicks,
            SourceIpv4: sourceIpv4,
            IsPartyMember: isPartyAccept);
        return true;
    }
}
