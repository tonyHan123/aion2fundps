// party_bulk_info.h — opcode 0x6a 0xe2 (DISABLED, was bulk roster).
//
// A2Viewer May 2026 RE confirmed this opcode is the friend-list /
// online-population broadcast, NOT a party-member broadcast. Treating
// it as party data surfaced strangers in the leaderboard
// ("친구목록 사람들이 미터기에 쭉 뜨네"). The C# reference left the
// parser as dead code behind an early return for diagnostic continuity;
// we mirror that — the handler exists so the dispatcher's bulk-debug
// log path still has a target, but emits nothing.
//
// Real party tracking flows through op=0297 (PartyAssembly) and
// op=0092 (CombatPower).

#ifndef AION2FUN_ENGINE_HANDLERS_PARTY_BULK_INFO_H
#define AION2FUN_ENGINE_HANDLERS_PARTY_BULK_INFO_H

#include <cstddef>
#include <cstdint>

namespace aion2fun::handlers {

// Returns false unconditionally (handler disabled). Kept for symmetry
// with the dispatcher's other handler entry points; if NCSoft ever
// changes 6a e2 semantics back to a member broadcast we can resurrect
// the legacy logic from C# reference behind a feature flag.
bool try_parse_party_bulk_info(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4) noexcept;

}  // namespace aion2fun::handlers

#endif
