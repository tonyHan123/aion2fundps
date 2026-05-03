// self_nickname.cpp — implementation.

#include "self_nickname.h"
#include "../varint.h"
#include "../nickname_validator.h"

namespace aion2fun::handlers {

bool try_parse_self_nickname(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::NicknameInfo& out_event) noexcept
{
    if (body == nullptr || body_length < 2) return false;
    if (body[0] != 0x33 || body[1] != 0x36) return false;

    size_t offset = 2;

    auto vi = try_read_varint(body + offset, body_length - offset);
    if (!vi.ok) return false;
    const int32_t user_id = vi.value;
    offset += vi.bytes_read;

    // Search next 10 bytes for the 0x07 spliter that precedes the name's
    // varint length. C# reference does the same linear scan.
    if (offset + 10 > body_length) return false;
    int spliter_idx = -1;
    for (int i = 0; i < 10; ++i) {
        if (body[offset + i] == 0x07) { spliter_idx = i; break; }
    }
    if (spliter_idx < 0) return false;
    offset += static_cast<size_t>(spliter_idx) + 1;

    vi = try_read_varint(body + offset, body_length - offset);
    if (!vi.ok) return false;
    const int32_t name_length = vi.value;
    offset += vi.bytes_read;

    if (name_length <= 0 || name_length > 71) return false;
    if (offset + static_cast<size_t>(name_length) > body_length) return false;

    if (!is_valid_nickname(body + offset, static_cast<size_t>(name_length))) {
        return false;
    }
    const uint8_t* nickname_ptr = body + offset;
    offset += static_cast<size_t>(name_length);

    // Server (uint16 LE) + job (1 byte) — both optional, default to -1
    // when packet is truncated. Matches C# reference.
    int32_t server = -1;
    int32_t job    = -1;
    if (offset + 2 <= body_length) {
        server = static_cast<int32_t>(
            static_cast<uint32_t>(body[offset])
            | (static_cast<uint32_t>(body[offset + 1]) << 8));
        offset += 2;
        if (offset < body_length) {
            job = body[offset];
        }
    }

    // Combat power lives at body_length - 40 as 4-byte LE. Range gate
    // (10k..999k) matches C# reference — values outside that mean we're
    // reading from a truncated packet and should report 0 ("unknown").
    int32_t combat_power = 0;
    if (body_length >= 50) {
        const size_t cp_offset = body_length - 40;
        const int32_t cp_raw = static_cast<int32_t>(
            static_cast<uint32_t>(body[cp_offset])
            | (static_cast<uint32_t>(body[cp_offset + 1]) << 8)
            | (static_cast<uint32_t>(body[cp_offset + 2]) << 16)
            | (static_cast<uint32_t>(body[cp_offset + 3]) << 24));
        if (cp_raw >= 10'000 && cp_raw <= 999'999) {
            combat_power = cp_raw;
        }
    }

    out_event.user_id          = user_id;
    out_event.nickname         = reinterpret_cast<const char*>(nickname_ptr);
    out_event.nickname_len     = name_length;
    out_event.is_self          = 1;
    out_event.server           = server;
    out_event.job              = job;
    out_event.combat_power     = combat_power;
    out_event.timestamp_ticks  = timestamp_ticks;
    out_event.source_ipv4      = source_ipv4;
    out_event.is_party_member  = 0;
    out_event.is_roster_start  = 0;
    out_event.room_id          = 0;
    return true;
}

}  // namespace aion2fun::handlers
