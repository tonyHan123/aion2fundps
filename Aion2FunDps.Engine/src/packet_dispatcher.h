// packet_dispatcher.h — Opcode routing + compressed-packet handling.
//
// Receives a complete game packet body (already varint-framed by
// FrameAssembler) and either:
//   1. Detects LZ4 compression marker → decompresses → recursively
//      dispatches each inner varint-framed packet, OR
//   2. Reads the 2-byte opcode and routes to the appropriate handler.
//
// Direct port of Aion2FunDps.Protocol.PacketDispatcher's three core
// methods: Dispatch / DispatchCompressed / DispatchOpcode. Per-opcode
// parsing logic (handlers) lives in handlers/*.cpp added in Phase 4 —
// Phase 3 only wires up the routing skeleton with stub callbacks so
// the dispatcher can be smoke-tested end-to-end before handler code
// arrives.
//
// Threading: single-threaded, callers must serialize Dispatch() calls.
// Callbacks fire on the calling thread.

#ifndef AION2FUN_ENGINE_PACKET_DISPATCHER_H
#define AION2FUN_ENGINE_PACKET_DISPATCHER_H

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>
#include "events.h"

namespace aion2fun {

// Diagnostic / parse-failure logging hook.
using DispatcherLogCallback = void(*)(
    void* ctx, int level, const char* message);

// Type-specific event callbacks. One per IGameEvent record. Caller fills
// in only the ones they want; a nullptr entry means "drop this event
// type silently". Pointers passed to the callbacks alias into the input
// packet body and are valid only for the duration of the call.
using DamageCallback         = void(*)(void* ctx, const events::DamageEvent*);
using MobHpCallback          = void(*)(void* ctx, const events::MobHpUpdate*);
using EncounterCallback      = void(*)(void* ctx, const events::EncounterAnnouncement*);
using CombatBoundaryCallback = void(*)(void* ctx, const events::CombatBoundary*);
using NicknameCallback       = void(*)(void* ctx, const events::NicknameInfo*);
using SummonSpawnCallback    = void(*)(void* ctx, const events::SummonSpawnInfo*);
using CombatPowerCallback    = void(*)(void* ctx, const events::CombatPowerUpdate*);
using PartyLeftCallback      = void(*)(void* ctx, const events::PartyLeft*);
using DungeonCallback        = void(*)(void* ctx, const events::DungeonAnnouncement*);
using PartyRosterCallback    = void(*)(void* ctx, const events::PartyRosterUpdate*);

// PartyAssembly aggregates its members into a single PartyRosterUpdate
// event (matching managed dispatcher behavior). The members array is a
// dispatcher-owned scratch vector; the C# side must marshal it during
// the callback. on_nickname still fires for SELF/OTHER/PartyMemberStatus
// individual nickname packets — the roster path bypasses it.
//
// Bundle of dispatcher callbacks. Caller fills in what they want; any
// nullptr entry is silently skipped (no-op dispatch).
struct DispatcherCallbacks {
    DispatcherLogCallback  on_log            = nullptr;
    DamageCallback         on_damage         = nullptr;
    MobHpCallback          on_mob_hp         = nullptr;
    EncounterCallback      on_encounter      = nullptr;
    CombatBoundaryCallback on_combat_boundary= nullptr;
    NicknameCallback       on_nickname       = nullptr;       // SELF/OTHER/PartyMemberStatus only
    SummonSpawnCallback    on_summon_spawn   = nullptr;
    CombatPowerCallback    on_combat_power   = nullptr;
    PartyLeftCallback      on_party_left     = nullptr;
    DungeonCallback        on_dungeon        = nullptr;
    PartyRosterCallback    on_party_roster   = nullptr;       // op=0297 / op=0197 aggregate emit
    void*                  ctx               = nullptr;
};

class PacketDispatcher {
public:
    PacketDispatcher() = default;
    PacketDispatcher(const PacketDispatcher&) = delete;
    PacketDispatcher& operator=(const PacketDispatcher&) = delete;

    // Entry point: feed one complete game-packet body extracted by the
    // FrameAssembler. body_length excludes the outer varint length
    // prefix already consumed by the assembler.
    void Dispatch(const uint8_t* body, int32_t body_length,
                  uint32_t source_ipv4, int64_t timestamp_ticks,
                  const DispatcherCallbacks& cbs);

    int64_t MalformedCount()    const noexcept { return malformed_count_; }
    int64_t Lz4SuccessCount()   const noexcept { return lz4_success_count_; }
    int64_t Lz4FailureCount()   const noexcept { return lz4_failure_count_; }

private:
    void DispatchCompressed(const uint8_t* compressed, int32_t compressed_len,
                            uint32_t source_ipv4, int64_t timestamp_ticks,
                            const DispatcherCallbacks& cbs);

    void DispatchOpcode(const uint8_t* body, int32_t body_length,
                        uint32_t source_ipv4, int64_t timestamp_ticks,
                        const DispatcherCallbacks& cbs);

    // Decompression scratch buffer reused across calls. Avoids per-packet
    // allocation when LZ4 packets stream in at game-tick rate during
    // combat. Grows on demand to fit origin_length; never shrinks because
    // typical session has consistent max packet size and the cost of
    // shrinking + regrowing exceeds the memory savings.
    std::vector<uint8_t> decompress_scratch_;

    // PartyRoster scratch — reused across op=0297 / op=0197 emits so we
    // don't reallocate the member vector each time. Never shrinks; clears
    // before each parse fills it.
    std::vector<events::NicknameInfo> roster_scratch_;

    // Self-presence tracking. Updated in op=33 36 (self nickname) parses,
    // read in op=01 97 multi-room scan to pick the user's own room block.
    // Mirror of managed SelfNicknameHandler.LastSelfUserId / LastSelfNickname.
    int32_t      last_self_user_id_   = 0;        // 0 = not yet seen
    std::string  last_self_nickname_;             // empty = not yet seen

    // Last lobby room id seen — set from op=02 97 / op=01 97 parses.
    // Used in op=01 97 to disambiguate between the user's room and
    // adjacent rooms when self_id isn't in the parsed members yet.
    int32_t      current_lobby_room_id_ = 0;

    int64_t malformed_count_   = 0;
    int64_t lz4_success_count_ = 0;
    int64_t lz4_failure_count_ = 0;
};

}  // namespace aion2fun

#endif  // AION2FUN_ENGINE_PACKET_DISPATCHER_H
