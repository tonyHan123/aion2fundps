// mob_hp.cpp — implementation.

#include "mob_hp.h"
#include "../varint.h"

namespace aion2fun::handlers {

bool try_parse_mob_hp(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::MobHpUpdate& out_event) noexcept
{
    if (body == nullptr || body_length < 2) return false;
    if (body[0] != 0x00 || body[1] != 0x8d) return false;

    size_t offset = 2;
    auto vi = try_read_varint(body + offset, body_length - offset);
    if (!vi.ok) return false;
    const int32_t mob_id = vi.value;
    offset += vi.bytes_read;

    // Skip 3 unknown varints
    for (int i = 0; i < 3; ++i) {
        vi = try_read_varint(body + offset, body_length - offset);
        if (!vi.ok) return false;
        offset += vi.bytes_read;
    }

    if (offset + 4 > body_length) return false;

    // uint32 LE → int64. Read as unsigned (HP non-negative); promote to
    // int64 to match C# `long hp` so downstream percentage math has
    // overflow headroom.
    const int64_t hp = static_cast<int64_t>(static_cast<uint32_t>(
        static_cast<uint32_t>(body[offset])
        | (static_cast<uint32_t>(body[offset + 1]) << 8)
        | (static_cast<uint32_t>(body[offset + 2]) << 16)
        | (static_cast<uint32_t>(body[offset + 3]) << 24)));

    out_event.mob_id          = mob_id;
    out_event.current_hp      = hp;
    out_event.timestamp_ticks = timestamp_ticks;
    out_event.source_ipv4     = source_ipv4;
    return true;
}

}  // namespace aion2fun::handlers
