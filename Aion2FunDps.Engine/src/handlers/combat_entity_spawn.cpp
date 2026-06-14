// combat_entity_spawn.cpp - implementation.

#include "combat_entity_spawn.h"
#include "../nickname_validator.h"

namespace aion2fun::handlers {

namespace {

inline int32_t read_i32_le(const uint8_t* p) noexcept {
    return static_cast<int32_t>(
        static_cast<uint32_t>(p[0])
        | (static_cast<uint32_t>(p[1]) << 8)
        | (static_cast<uint32_t>(p[2]) << 16)
        | (static_cast<uint32_t>(p[3]) << 24));
}

inline int32_t read_u16_le(const uint8_t* p) noexcept {
    return static_cast<int32_t>(
        static_cast<uint32_t>(p[0])
        | (static_cast<uint32_t>(p[1]) << 8));
}

inline bool is_known_server_id(int32_t id) noexcept {
    return (id >= 1001 && id <= 1021) || (id >= 2001 && id <= 2021);
}

}  // anonymous namespace

bool try_parse_combat_entity_spawn(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::NicknameInfo& out_event) noexcept
{
    if (body == nullptr || body_length < 16) return false;
    if (body[0] != 0x22 || body[1] != 0x92) return false;
    if (body[12] != 0x24) return false;

    const int32_t actor_id = read_i32_le(body + 4);
    if (actor_id <= 0 || actor_id > 100'000'000) return false;

    const size_t seg_off = 13 + 36;
    if (seg_off + 9 > body_length) return false;

    const int32_t name_len = body[seg_off + 8];
    if (name_len < 1 || name_len > 50) return false;

    const size_t nick_off = seg_off + 9;
    if (nick_off + static_cast<size_t>(name_len) > body_length) return false;
    if (!is_valid_nickname(body + nick_off, static_cast<size_t>(name_len))) return false;

    int32_t server = 0;
    const int32_t server_candidate = read_u16_le(body + seg_off + 6);
    if (is_known_server_id(server_candidate)) {
        server = server_candidate;
    }

    int32_t job = 0;
    const size_t job_off = nick_off + static_cast<size_t>(name_len);
    if (job_off + 4 <= body_length) {
        const int32_t job_raw = read_i32_le(body + job_off);
        if (job_raw >= 1 && job_raw <= 40) {
            job = job_raw;
        }
    }

    out_event.user_id          = actor_id;
    out_event.nickname         = reinterpret_cast<const char*>(body + nick_off);
    out_event.nickname_len     = name_len;
    out_event.is_self          = 0;
    out_event.server           = server;
    out_event.job              = job;
    out_event.combat_power     = 0;
    out_event.timestamp_ticks  = timestamp_ticks;
    out_event.source_ipv4      = source_ipv4;
    out_event.is_party_member  = 0;
    out_event.is_roster_start  = 0;
    out_event.room_id          = 0;
    return true;
}

}  // namespace aion2fun::handlers
