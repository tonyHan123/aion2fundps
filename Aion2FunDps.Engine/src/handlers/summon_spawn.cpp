// summon_spawn.cpp — implementation.

#include "summon_spawn.h"
#include "../varint.h"
#include "../nickname_validator.h"

#include <cstring>

namespace aion2fun::handlers {

namespace {

// Find first occurrence of `needle` in `haystack`. Tiny linear search;
// expected needle size is 3 or 8 bytes, haystack ≤ 1 KB typical.
// Returns offset within haystack on hit, or -1.
int memmem_simple(const uint8_t* haystack, size_t hay_len,
                  const uint8_t* needle,   size_t needle_len) noexcept
{
    if (needle_len == 0 || hay_len < needle_len) return -1;
    const size_t last = hay_len - needle_len;
    for (size_t i = 0; i <= last; ++i) {
        if (std::memcmp(haystack + i, needle, needle_len) == 0) {
            return static_cast<int>(i);
        }
    }
    return -1;
}

// Owner-id marker scan: [FF×8] [07 02 06] [low] [high] = owner_id.
// Reject candidates ≤ 99 (real entity ids are 4-6 digits). 0 means
// "owner not extractable from this packet".
int32_t scan_owner_id(const uint8_t* body, size_t body_length) noexcept {
    static constexpr uint8_t kBoundaryMarker[8] = {
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
    };
    static constexpr uint8_t kActorHeader[3] = { 0x07, 0x02, 0x06 };

    size_t search_pos = 0;
    while (search_pos < body_length) {
        const int boundary_rel = memmem_simple(
            body + search_pos, body_length - search_pos,
            kBoundaryMarker, sizeof(kBoundaryMarker));
        if (boundary_rel < 0) return 0;

        const size_t boundary_abs   = search_pos + static_cast<size_t>(boundary_rel);
        const size_t after_boundary = boundary_abs + sizeof(kBoundaryMarker);
        if (after_boundary >= body_length) return 0;

        const int header_rel = memmem_simple(
            body + after_boundary, body_length - after_boundary,
            kActorHeader, sizeof(kActorHeader));
        if (header_rel < 0) {
            // No actor header in remainder — try scanning past this
            // boundary in case there are multiple FF×8 sentinels.
            search_pos = after_boundary;
            continue;
        }
        const size_t header_abs = after_boundary + static_cast<size_t>(header_rel);
        // Need 5 bytes from header start: [07][02][06][low][high]
        if (header_abs + 5 > body_length) return 0;

        const int32_t cand = static_cast<int32_t>(
            static_cast<uint32_t>(body[header_abs + 3])
            | (static_cast<uint32_t>(body[header_abs + 4]) << 8));
        if (cand > 99) return cand;

        search_pos = boundary_abs + 1;
    }
    return 0;
}

}  // anonymous namespace

bool try_parse_summon_spawn(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::SummonSpawnInfo& out_event) noexcept
{
    if (body == nullptr || body_length < 12) return false;
    if (body[0] != 0x40 || body[1] != 0x36) return false;

    const auto vi = try_read_varint(body + 2, body_length - 2);
    if (!vi.ok) return false;
    const int32_t entity_id   = vi.value;
    const size_t  after_varint = 2 + static_cast<size_t>(vi.bytes_read);

    // Layout after VarInt: [type_byte] [size_low] [mode_flag] ...
    if (after_varint + 3 > body_length) return false;
    const uint8_t mode_flag = body[after_varint + 2];

    size_t mob_code_offset = 0;
    const uint8_t* owner_name_ptr = nullptr;
    int32_t owner_name_len = 0;

    if (mode_flag == 0x00) {
        mob_code_offset = after_varint + 3;
    }
    else if (mode_flag == 0x01) {
        const size_t name_len_offset = after_varint + 3;
        if (name_len_offset >= body_length) return false;
        const int32_t name_len = body[name_len_offset];
        if (name_len < 1 || name_len > 50) return false;

        const size_t name_offset = name_len_offset + 1;
        if (name_offset + static_cast<size_t>(name_len) > body_length) return false;

        // Strict validator rejects misaligned slices (control bytes /
        // punctuation that no real Aion 2 nickname would contain).
        if (is_valid_nickname(body + name_offset, static_cast<size_t>(name_len))) {
            owner_name_ptr = body + name_offset;
            owner_name_len = name_len;
        }
        // Even if the name didn't validate, we keep the offset advancement
        // — mob_code position is determined by the wire layout, not by
        // whether we successfully extracted the name.

        mob_code_offset = name_offset + static_cast<size_t>(name_len);
    }
    else {
        return false;
    }

    if (mob_code_offset + 4 > body_length) return false;

    const int32_t mob_code = static_cast<int32_t>(
        static_cast<uint32_t>(body[mob_code_offset])
        | (static_cast<uint32_t>(body[mob_code_offset + 1]) << 8)
        | (static_cast<uint32_t>(body[mob_code_offset + 2]) << 16)
        | (static_cast<uint32_t>(body[mob_code_offset + 3]) << 24));
    if (mob_code < 1'000'000 || mob_code > 50'000'000) return false;

    const int32_t owner_id = scan_owner_id(body, body_length);

    out_event.summon_id        = entity_id;
    out_event.owner_id         = owner_id;
    out_event.mob_code         = mob_code;
    out_event.timestamp_ticks  = timestamp_ticks;
    out_event.source_ipv4      = source_ipv4;
    out_event.owner_name       = reinterpret_cast<const char*>(owner_name_ptr);
    out_event.owner_name_len   = owner_name_len;
    return true;
}

}  // namespace aion2fun::handlers
