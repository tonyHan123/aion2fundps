// dot.cpp — implementation.

#include "dot.h"
#include "../varint.h"

namespace aion2fun::handlers {

bool try_parse_dot(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::DamageEvent& out_event) noexcept
{
    if (body == nullptr || body_length < 2) return false;
    if (body[0] != 0x05 || body[1] != 0x38) return false;

    size_t offset = 2;

    auto read_vi = [&](int32_t& out) -> bool {
        const auto vi = try_read_varint(body + offset, body_length - offset);
        if (!vi.ok) return false;
        out = vi.value;
        offset += vi.bytes_read;
        return true;
    };

    int32_t target_id;
    if (!read_vi(target_id)) return false;
    if (offset >= body_length) return false;

    // Bit flag must have 0x02 set; otherwise this isn't a real DOT we count.
    const uint8_t flag_byte = body[offset];
    if ((flag_byte & 0x02) == 0) return false;
    ++offset;

    int32_t actor_id;
    if (!read_vi(actor_id)) return false;
    if (actor_id == target_id) return false;

    int32_t unknown;
    if (!read_vi(unknown)) return false;

    if (offset + 4 > body_length) return false;
    const uint32_t skill_code_candidate =
        static_cast<uint32_t>(body[offset])
        | (static_cast<uint32_t>(body[offset + 1]) << 8)
        | (static_cast<uint32_t>(body[offset + 2]) << 16)
        | (static_cast<uint32_t>(body[offset + 3]) << 24);
    // TK divides by 10 (display layer further /100s if needed).
    const uint32_t skill_code = skill_code_candidate / 10;
    offset += 4;

    int32_t damage;
    if (!read_vi(damage)) return false;
    if (damage < 0 || damage >= 10'000'000) return false;

    out_event.actor_id        = actor_id;
    out_event.target_id       = target_id;
    out_event.damage          = damage;
    out_event.skill_code      = skill_code;
    out_event.type            = 0;
    out_event.specials        = events::DF_None;
    out_event.loop            = 0;
    out_event.is_dot          = 1;
    out_event.timestamp_ticks = timestamp_ticks;
    out_event.source_ipv4     = source_ipv4;
    return true;
}

}  // namespace aion2fun::handlers
