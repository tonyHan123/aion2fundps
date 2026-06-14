// combat_entity_spawn.h - opcode 0x22 0x92 in-dungeon entity spawn.
//
// This packet bridges a live combat actor id to the player's nickname,
// canonical id, server, and job. It is info-enrichment only and emits a
// NicknameInfo with user_id set to the combat actor id used by damage events.

#ifndef AION2FUN_ENGINE_HANDLERS_COMBAT_ENTITY_SPAWN_H
#define AION2FUN_ENGINE_HANDLERS_COMBAT_ENTITY_SPAWN_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_combat_entity_spawn(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::NicknameInfo& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
