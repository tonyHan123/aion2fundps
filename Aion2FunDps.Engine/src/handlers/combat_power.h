// combat_power.h — opcode 0x00 0x92 (dedicated combat-power broadcast).
//
// Layout: stats trailer ending in magic bytes [06 00 36] with 5-byte
// zero pad immediately before it. Reading backward from the magic:
//   num-21..-18  level (4 LE, must be 1..55)
//   num-17..-14  zero (must be 0)
//   num-13..-10  item level (4 LE, must be 1000..5000) — sanity gate
//   num-9..-6    CP (4 LE, must be 10_000..999_999)
//   num-5..-1    zero pad
//   num..num+2   magic 06 00 36
//
// Nickname appears earlier as [server 2 LE][nameLen 1][UTF-8] — the parser
// scans bytes preceding the stats trailer for a plausible (server, name)
// pair.
//
// Direct port of CombatPowerHandler.cs.

#ifndef AION2FUN_ENGINE_HANDLERS_COMBAT_POWER_H
#define AION2FUN_ENGINE_HANDLERS_COMBAT_POWER_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_combat_power(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::CombatPowerUpdate& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
