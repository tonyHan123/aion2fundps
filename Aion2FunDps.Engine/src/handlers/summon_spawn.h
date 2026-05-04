// summon_spawn.h — opcode 0x40 0x36 (entity spawn: mobs / NPCs / summons).
//
// Two captured wire variants (KR live, 2026-04 빌드):
//
// Variant A (no name) — mobs/NPCs without an owner:
//   offset 0-1     opcode 40 36
//   offset 2..     VarInt entityId
//   +1             type byte (0x0c / 0x04 / 0x22 observed)
//   +2             0x__ 0x00       (size_low, mode_flag = 0x00)
//   +4             mob_code (4-byte LE)
//   ... payload
//
// Variant B (named, owner-attributed) — summons/pets with embedded owner name:
//   offset 0-1     opcode 40 36
//   offset 2..     VarInt entityId
//   +1             type byte (0x5f and others observed)
//   +2             0x00 0x01       (size_low, mode_flag = 0x01 → name follows)
//   +4             name length (1 byte)
//   +5..+5+L       UTF-8 owner name
//   +5+L..+8+L     mob_code (4-byte LE)
//
// Owner-id scan (separate from name-based attribution):
// [FF×8] [07 02 06] markers, then 2 LE bytes = owner_id. Algorithm ported
// from A2Viewer.Packet.PacketDispatcher.TryParseSummon. Catches summons
// (정령성 spirits) whose wire emits the owner ONLY as numeric id without
// the embedded name.
//
// Direct port of SummonSpawnHandler.cs.

#ifndef AION2FUN_ENGINE_HANDLERS_SUMMON_SPAWN_H
#define AION2FUN_ENGINE_HANDLERS_SUMMON_SPAWN_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_summon_spawn(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::SummonSpawnInfo& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
