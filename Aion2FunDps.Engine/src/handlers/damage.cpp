// damage.cpp — implementation.

#include "damage.h"
#include "../varint.h"

namespace aion2fun::handlers {

bool try_parse_damage(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    bool is_dot,
    events::DamageEvent& out_event) noexcept
{
    if (body == nullptr || body_length < 2) return false;
    if (body[0] != 0x04 || body[1] != 0x38) return false;

    size_t offset = 2;

    auto read_vi = [&](int32_t& out) -> bool {
        const auto vi = try_read_varint(body + offset, body_length - offset);
        if (!vi.ok) return false;
        out = vi.value;
        offset += vi.bytes_read;
        return true;
    };

    int32_t target_id, switch_var, flag, actor_id;
    if (!read_vi(target_id)) return false;
    if (!read_vi(switch_var)) return false;
    if (!read_vi(flag)) return false;
    if (!read_vi(actor_id)) return false;

    if (offset + 5 > body_length) return false;
    const uint32_t skill_code =
        static_cast<uint32_t>(body[offset])
        | (static_cast<uint32_t>(body[offset + 1]) << 8)
        | (static_cast<uint32_t>(body[offset + 2]) << 16)
        | (static_cast<uint32_t>(body[offset + 3]) << 24);
    offset += 5;  // 4-byte skill code + 1 separator byte

    int32_t type;
    if (!read_vi(type)) return false;

    // Specials block size keyed off switchVar & 7. Same magic numbers as
    // C# reference (DamageHandler.cs:50).
    int specials_size;
    switch (switch_var & 7) {
        case 4: specials_size = 8;  break;
        case 5: specials_size = 12; break;
        case 6: specials_size = 10; break;
        case 7: specials_size = 14; break;
        default: return false;
    }
    if (offset + static_cast<size_t>(specials_size) > body_length) return false;

    const uint8_t specials = body[offset];
    offset += static_cast<size_t>(specials_size);

    int32_t unknown, damage, loop;
    if (!read_vi(unknown)) return false;
    if (!read_vi(damage)) return false;
    if (!read_vi(loop)) return false;

    // Validity guards (from TK):
    if (actor_id == target_id) return false;
    if (damage < 0 || damage >= 10'000'000) return false;

    out_event.actor_id        = actor_id;
    out_event.target_id       = target_id;
    out_event.damage          = damage;
    out_event.skill_code      = skill_code;
    out_event.type            = type;
    out_event.specials        = specials;
    out_event.loop            = loop;
    out_event.is_dot          = is_dot ? 1 : 0;
    out_event.timestamp_ticks = timestamp_ticks;
    out_event.source_ipv4     = source_ipv4;
    return true;
}

}  // namespace aion2fun::handlers
