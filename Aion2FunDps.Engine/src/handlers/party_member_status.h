// party_member_status.h — opcode family 0x__ 0x97 / 0x__ 0xe2 (info enrichment).
//
// Captures nearby-player / friend-status / live-ping broadcasts to keep
// NicknameRegistry fresh — entityId → (nickname, server, job, CP) — so
// damage attribution and class-icon rendering work when distant party
// members deal damage outside OTHER_NICK's visibility cone.
//
// Layout (KR live, May 2026):
//   offset 0-1     opcode (78 e2 / 0b 97 / 1f 97 / etc.)
//   offset 2-3     sub-opcode
//   offset 4-7     entityId 4-byte LE
//   offset 8-9     padding (00 00)
//   offset 10-11   serverId 2-byte LE
//   offset 12      name length (1 byte)
//   offset 13..    UTF-8 nickname
//   statsOffset+0  jobCode 4 LE (1..40)
//   statsOffset+12 trailer flag (0x01 → CP available)
//   statsOffset+18 CP 4 LE
//
// Excluded opcodes (handled elsewhere or non-member):
//   02 97 (PartyAssembly), 6a e2 (bulk friend), 07 97 (request),
//   13 97 / 2A 97 (board control), 1D 97 (party leave), 04 97 (dungeon exit).
//
// Only 0B 97 (PartyAccept) sets IsPartyMember=true — see C# reference for
// the wire-confirmed rationale (closes a 17-second gap before next 0197).

#ifndef AION2FUN_ENGINE_HANDLERS_PARTY_MEMBER_STATUS_H
#define AION2FUN_ENGINE_HANDLERS_PARTY_MEMBER_STATUS_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_party_member_status(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::NicknameInfo& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
