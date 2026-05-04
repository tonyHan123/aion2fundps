// encounter_announce.h — opcode 0x01 0x91 (boss / encounter announce).
//
// Two layouts observed (TK + ours, RE 2026-04-29):
//   Layout A (1-boss, len=31):  [opcode][pad 2B][hdr 5B][count=01][prefix VL][mobCode 4B LE][tail]
//   Layout B (multi-boss):       [opcode][pad 2B][hdr 5B][count=N][per-boss block × N][tail]
//
// We only need the FIRST mob_code, so we walk past the count byte at
// offset 9, read one varint prefix, then 4-byte LE mob_code immediately
// after. Sanity-gated to mobs.json range (1M..50M).
//
// Direct port of EncounterAnnounceHandler.cs.

#ifndef AION2FUN_ENGINE_HANDLERS_ENCOUNTER_ANNOUNCE_H
#define AION2FUN_ENGINE_HANDLERS_ENCOUNTER_ANNOUNCE_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_encounter_announce(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::EncounterAnnouncement& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
