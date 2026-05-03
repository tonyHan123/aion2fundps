// combat_boundary.h — opcode 0x21 0x8d (combat start/end)
//
// Layout: [opcode 2B] [mobId varint]
//
// Direct port of CombatBoundaryHandler.cs.

#ifndef AION2FUN_ENGINE_HANDLERS_COMBAT_BOUNDARY_H
#define AION2FUN_ENGINE_HANDLERS_COMBAT_BOUNDARY_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_combat_boundary(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::CombatBoundary& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
