// party_assembly.h — opcode 0x02 0x97 (matchmaking/party-assembly broadcast).
//
// Issued by NCSoft's lobby server. Carries the matchmaking room name plus
// per-member records (nickname + entity_id + level + 8-byte account ID +
// zone). Discovering this opcode is what makes "party visible in
// matchmaking room" work — without it the leaderboard stays blank until
// the user's first hit on a boss.
//
// This handler emits MANY events per packet (one NicknameInfo per
// member), so it uses a callback-per-member shape unlike the single-event
// handlers in this directory.
//
// Direct port of PartyAssemblyHandler.cs. The byte-scanner approach
// (vs. exact record-boundary parsing) is intentional — record sizes vary
// across observed packets and a strict layout would miss members. The
// dedupe set + plausibility gates make over-emission impossible while
// still catching every legitimate record.

#ifndef AION2FUN_ENGINE_HANDLERS_PARTY_ASSEMBLY_H
#define AION2FUN_ENGINE_HANDLERS_PARTY_ASSEMBLY_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

// Per-member callback fired for each parsed NicknameInfo within a single
// op=0297 / op=0197 broadcast. The NicknameInfo's nickname pointer aliases
// into the input body and is valid only for the duration of the call.
using PartyMemberCallback = void(*)(void* ctx, const events::NicknameInfo*);

// Parse op=0297 broadcast. Returns the parsed group_id (0 if packet is
// malformed). Members are emitted via member_cb in scan order. If
// member_cb is null, the function still parses (group_id) but emits
// nothing — useful when the caller only needs the room id.
int32_t try_parse_party_assembly(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    PartyMemberCallback member_cb,
    void* ctx) noexcept;

// Generic per-member scanner used by both op=0297 (PartyAssembly) and
// op=0197 (encounter announce roster). Caller specifies the start offset
// past the opcode-specific header. group_id_hint=0 is fine when the
// caller has no room context.
void scan_for_nicknames(
    const uint8_t* body, size_t body_length,
    size_t start_pos, size_t end_pos,
    int32_t group_id_hint,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    PartyMemberCallback member_cb,
    void* ctx) noexcept;

// op=0197 dungeon id extraction (matches A2Viewer's ScanDungeonIdRaw).
// Returns 0 when no dungeon signal present.
int32_t extract_dungeon_id(const uint8_t* body, size_t body_length) noexcept;

}  // namespace aion2fun::handlers

#endif
