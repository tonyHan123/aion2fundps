// dot.h — opcode 0x05 0x38 (DOT damage tick).
//
// Layout (TK parseDoTPacket):
//   [opcode 2B] [targetId varint] [bitFlag byte, must have 0x02 set]
//   [actorId varint, != target] [unknown varint]
//   [skillCodeCandidate uint32 LE, /10] [damage varint]
//
// Emits a DamageEvent with is_dot=true. Different from main damage in
// that there's no specials block — DOTs are computed elsewhere on the
// server, we only see the result.

#ifndef AION2FUN_ENGINE_HANDLERS_DOT_H
#define AION2FUN_ENGINE_HANDLERS_DOT_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_dot(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::DamageEvent& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
