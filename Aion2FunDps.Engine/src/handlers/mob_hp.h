// mob_hp.h — opcode 0x00 0x8d (mob remaining HP)
//
// Layout (TK-open-public reference):
//   [opcode 2B] [mobId varint] [unknown varint × 3] [currentHp uint32 LE]
//
// Direct port of MobHpHandler.cs.

#ifndef AION2FUN_ENGINE_HANDLERS_MOB_HP_H
#define AION2FUN_ENGINE_HANDLERS_MOB_HP_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_mob_hp(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::MobHpUpdate& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
