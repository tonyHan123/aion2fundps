// party_member_status.cpp — implementation.

#include "party_member_status.h"
#include "../nickname_validator.h"

namespace aion2fun::handlers {

namespace {
// Aion 2 KR server id ranges (mirror of ServerMap._names keys):
//   1001..1021 (group A) and 2001..2021 (group B). Anything else is
//   "unknown / corrupt" and we fall back to 0.
inline bool is_known_server_id(int32_t id) noexcept {
    return (id >= 1001 && id <= 1021) || (id >= 2001 && id <= 2021);
}
}  // anonymous namespace

bool try_parse_party_member_status(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::NicknameInfo& out_event) noexcept
{
    if (body == nullptr || body_length < 16) return false;

    const uint8_t op0 = body[0];
    const uint8_t op1 = body[1];

    // Opcode acceptance — same set as PartyMemberStatusHandler.cs. See
    // that file for per-opcode rationale (e.g., why 07 97 is rejected
    // even though it carries member-shaped info).
    const bool is_handled =
        (op1 == 0xe2 && op0 != 0x6a) ||
        (op1 == 0x97
            && op0 != 0x02 && op0 != 0x07 && op0 != 0x13
            && op0 != 0x1d && op0 != 0x2a && op0 != 0x04);
    if (!is_handled) return false;

    // entityId at offset 4-7 (4-byte LE)
    const int32_t entity_id = static_cast<int32_t>(
        static_cast<uint32_t>(body[4])
        | (static_cast<uint32_t>(body[5]) << 8)
        | (static_cast<uint32_t>(body[6]) << 16)
        | (static_cast<uint32_t>(body[7]) << 24));
    if (entity_id <= 0 || entity_id > 100'000'000) return false;

    const int32_t name_len = body[12];
    if (name_len < 1 || name_len > 50) return false;
    if (13 + static_cast<size_t>(name_len) > body_length) return false;

    if (!is_valid_nickname(body + 13, static_cast<size_t>(name_len))) return false;

    // server id at offset 10-11 (LE uint16). Sanity-gate via known set.
    int32_t server_id = 0;
    {
        const int32_t cand = static_cast<int32_t>(
            static_cast<uint32_t>(body[10])
            | (static_cast<uint32_t>(body[11]) << 8));
        if (is_known_server_id(cand)) server_id = cand;
    }

    // jobCode at statsOffset+0. statsOffset = 13 + name_len.
    const size_t stats_offset = 13 + static_cast<size_t>(name_len);

    int32_t job = 0;
    if (stats_offset + 4 <= body_length) {
        const int32_t job_raw = static_cast<int32_t>(
            static_cast<uint32_t>(body[stats_offset])
            | (static_cast<uint32_t>(body[stats_offset + 1]) << 8)
            | (static_cast<uint32_t>(body[stats_offset + 2]) << 16)
            | (static_cast<uint32_t>(body[stats_offset + 3]) << 24));
        if (job_raw >= 1 && job_raw <= 40) job = job_raw;
    }

    // CP at statsOffset+18 only when LiveStatus trailer flag (0x01) is at
    // statsOffset+12.
    int32_t combat_power = 0;
    if (stats_offset + 13 <= body_length && body[stats_offset + 12] == 0x01) {
        const size_t cp_offset = stats_offset + 18;
        if (cp_offset + 4 <= body_length) {
            const int32_t cp_raw = static_cast<int32_t>(
                static_cast<uint32_t>(body[cp_offset])
                | (static_cast<uint32_t>(body[cp_offset + 1]) << 8)
                | (static_cast<uint32_t>(body[cp_offset + 2]) << 16)
                | (static_cast<uint32_t>(body[cp_offset + 3]) << 24));
            if (cp_raw >= 10'000 && cp_raw <= 999'999) combat_power = cp_raw;
        }
    }

    // Only 0B 97 (PartyAccept) flips IsPartyMember=true to surface a new
    // joiner before the next 0197 roster broadcast (closes a 17s gap per
    // log 2026-05-02 01:30:15). Other opcodes stay false; idempotent
    // because RoomLifecycleTracker.AddLiveMember is a no-op for already-
    // joined entityIds.
    const bool is_party_accept = (op0 == 0x0B && op1 == 0x97);

    out_event.user_id          = entity_id;
    out_event.nickname         = reinterpret_cast<const char*>(body + 13);
    out_event.nickname_len     = name_len;
    out_event.is_self          = 0;
    out_event.server           = server_id;
    out_event.job              = job;
    out_event.combat_power     = combat_power;
    out_event.timestamp_ticks  = timestamp_ticks;
    out_event.source_ipv4      = source_ipv4;
    out_event.is_party_member  = is_party_accept ? 1 : 0;
    out_event.is_roster_start  = 0;
    out_event.room_id          = 0;
    return true;
}

}  // namespace aion2fun::handlers
