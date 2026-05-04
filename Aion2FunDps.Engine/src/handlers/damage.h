// damage.h — opcode 0x04 0x38 (main damage packet).
//
// Layout (TK-open-public StreamProcessor.kt):
//   [opcode 2B] [targetId varint] [switchVar varint] [flag varint] [actorId varint]
//   [skillCode uint32 LE + 1 separator byte = 5B] [type varint]
//   [specials block, size = 8/10/12/14 from switchVar & 7]
//   [unknown varint] [damage varint] [loop varint]
//
// is_dot is a caller-provided flag (always false here; DotHandler emits
// a DamageEvent with is_dot=true).

#ifndef AION2FUN_ENGINE_HANDLERS_DAMAGE_H
#define AION2FUN_ENGINE_HANDLERS_DAMAGE_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_damage(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    bool is_dot,
    events::DamageEvent& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
