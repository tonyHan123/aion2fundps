// other_nickname.cpp — implementation.

#include "other_nickname.h"
#include "../varint.h"
#include "../nickname_validator.h"

namespace aion2fun::handlers {

bool try_parse_other_nickname(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::NicknameInfo& out_event) noexcept
{
    if (body == nullptr || body_length < 2) return false;
    if (body[0] != 0x44 || body[1] != 0x36) return false;

    size_t offset = 2;

    auto vi = try_read_varint(body + offset, body_length - offset);
    if (!vi.ok) return false;
    const int32_t user_id = vi.value;
    offset += vi.bytes_read;

    // Skip unknown1, unknown2 varints
    vi = try_read_varint(body + offset, body_length - offset);
    if (!vi.ok) return false;
    offset += vi.bytes_read;

    vi = try_read_varint(body + offset, body_length - offset);
    if (!vi.ok) return false;
    offset += vi.bytes_read;

    if (body_length - offset <= 2) return false;
    offset += 1;  // skip 1 byte (per TK)

    // Brute-force probe offsets 0..4 for a valid nickname varint length.
    // The strict UTF-8 validator (Hangul + ASCII) below is what makes
    // this safe — without it, misaligned probes would accept garbage
    // slices like "!까불" leaking phantom party rows.
    const size_t base_offset = offset;
    const uint8_t* nickname_ptr = nullptr;
    int32_t nickname_len = 0;
    size_t nick_end = 0;

    for (int i = 0; i < 5; ++i) {
        const size_t probe = base_offset + static_cast<size_t>(i);
        if (probe >= body_length) continue;

        const auto pvi = try_read_varint(body + probe, body_length - probe);
        if (!pvi.ok) continue;

        const int32_t name_len = pvi.value;
        if (name_len < 1 || name_len > 71) continue;

        const size_t name_start = probe + pvi.bytes_read;
        if (name_start + static_cast<size_t>(name_len) > body_length) continue;

        if (!is_valid_nickname(body + name_start, static_cast<size_t>(name_len))) continue;

        nickname_ptr = body + name_start;
        nickname_len = name_len;
        nick_end     = name_start + static_cast<size_t>(name_len);
        break;
    }
    if (nickname_ptr == nullptr) return false;

    offset = nick_end;
    if (offset >= body_length) return false;
    const int32_t job = body[offset];
    offset += 1;

    // Brute-force search for valid server (1001..1021 OR 2001..2021).
    // Default to 0 (NOT -1) so the aggregator's `Server == 0 → keep prev`
    // fallback applies — wire-confirmed bug fix from 2026-05-04 무의요람.
    int32_t server = 0;
    const size_t server_base = offset;
    for (int i = 0; i < 32; ++i) {
        const size_t probe = server_base + static_cast<size_t>(i);
        if (probe + 2 > body_length) break;
        const int32_t cand = static_cast<int32_t>(
            static_cast<uint32_t>(body[probe])
            | (static_cast<uint32_t>(body[probe + 1]) << 8));
        const bool in_range =
            (cand >= 1001 && cand <= 1021) ||
            (cand >= 2001 && cand <= 2021);
        if (!in_range) continue;
        server = cand;
        break;
    }

    // CP at tail-40 (same as SELF_NICK).
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
    out_event.nickname_len     = nickname_len;
    out_event.is_self          = 0;
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
