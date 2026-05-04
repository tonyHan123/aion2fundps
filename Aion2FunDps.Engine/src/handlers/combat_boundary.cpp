// combat_boundary.cpp — implementation.

#include "combat_boundary.h"
#include "../varint.h"

namespace aion2fun::handlers {

bool try_parse_combat_boundary(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::CombatBoundary& out_event) noexcept
{
    if (body == nullptr || body_length < 2) return false;
    if (body[0] != 0x21 || body[1] != 0x8d) return false;

    const auto vi = try_read_varint(body + 2, body_length - 2);
    if (!vi.ok) return false;

    out_event.mob_id          = vi.value;
    out_event.timestamp_ticks = timestamp_ticks;
    out_event.source_ipv4     = source_ipv4;
    return true;
}

}  // namespace aion2fun::handlers
