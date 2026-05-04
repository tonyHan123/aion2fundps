// engine_api.h — Public C ABI surface of Aion2FunDps.Engine.dll.
//
// Why a flat C API instead of C++ class export:
//   - C# P/Invoke targets `extern "C"` symbols with cdecl/stdcall calling
//     conventions. Exporting C++ classes would require C++/CLI bridging or
//     mangled-name imports — both fragile across compiler versions.
//   - Keeping the surface tiny means changes to internal C++ types don't
//     break the ABI; we can refactor freely as long as these functions
//     keep their shape.
//   - Mirrors A2Power's PacketEngine.dll style (PP_Init, PP_Feed, etc.)
//     which we know works in production.
//
// Memory ownership rule: any const pointer the engine returns to the C#
// caller via callback is valid only for the duration of the callback in
// which it was emitted. The engine reuses internal buffers between
// events; the C# side must marshal the bytes/string immediately and not
// retain the pointer.
//
// Threading: dispatcher / frame-assembler instances are single-threaded.
// Capture callers must serialize calls per instance. Callbacks fire on
// the calling thread.

#ifndef AION2FUN_ENGINE_API_H
#define AION2FUN_ENGINE_API_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// Visibility macro.
#if defined(AION2FUN_ENGINE_BUILDING)
#  define AION2FUN_ENGINE_EXPORT __declspec(dllexport)
#else
#  define AION2FUN_ENGINE_EXPORT
#endif

// =====================================================================
// Lifecycle / log
// =====================================================================

typedef void (*Aion2FunOnLog)(void* ctx, int level, const char* message);

AION2FUN_ENGINE_EXPORT uint32_t Aion2Fun_GetVersion(void);
AION2FUN_ENGINE_EXPORT int32_t  Aion2Fun_Init(void);
AION2FUN_ENGINE_EXPORT void     Aion2Fun_Shutdown(void);
AION2FUN_ENGINE_EXPORT void     Aion2Fun_SetLogCallback(Aion2FunOnLog cb, void* ctx);

// =====================================================================
// Opaque handles
// =====================================================================
//
// Handles wrap heap-allocated C++ objects (PacketDispatcher, FrameAssembler)
// behind void* so the C ABI doesn't expose any C++ types. C# treats them
// as IntPtr.
typedef void* Aion2FunDispatcherHandle;
typedef void* Aion2FunFrameAssemblerHandle;

// =====================================================================
// Event POD layouts
// =====================================================================
//
// These structs MUST stay layout-compatible with src/events.h. Rather
// than dual-define them, the .cpp side static_asserts equivalence at
// build time (see engine_api.cpp). For C# P/Invoke we declare matching
// `[StructLayout(LayoutKind.Sequential)]` records.

typedef struct {
    int32_t  actor_id;
    int32_t  target_id;
    int32_t  damage;
    uint32_t skill_code;
    int32_t  type;
    uint8_t  specials;
    int32_t  loop;
    uint8_t  is_dot;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
} Aion2FunDamageEvent;

typedef struct {
    int32_t  mob_id;
    int64_t  current_hp;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
} Aion2FunMobHpUpdate;

typedef struct {
    int32_t  mob_code;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
} Aion2FunEncounterAnnouncement;

typedef struct {
    int32_t  mob_id;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
} Aion2FunCombatBoundary;

typedef struct {
    int32_t  user_id;
    const char* nickname;
    int32_t  nickname_len;
    uint8_t  is_self;
    int32_t  server;
    int32_t  job;
    int32_t  combat_power;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
    uint8_t  is_party_member;
    uint8_t  is_roster_start;
    int32_t  room_id;
} Aion2FunNicknameInfo;

typedef struct {
    int32_t  summon_id;
    int32_t  owner_id;
    int32_t  mob_code;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
    const char* owner_name;
    int32_t  owner_name_len;
} Aion2FunSummonSpawnInfo;

typedef struct {
    const char* nickname;
    int32_t  nickname_len;
    int32_t  server_id;
    int32_t  combat_power;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
} Aion2FunCombatPowerUpdate;

typedef struct {
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
} Aion2FunPartyLeft;

typedef struct {
    int32_t  dungeon_id;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
} Aion2FunDungeonAnnouncement;

// PartyRosterUpdate carries an array of NicknameInfo members. The members
// pointer aliases into a dispatcher-owned scratch vector and is valid only
// during the callback. Confidence values: 0=Strong (op=0297 / 6ae2),
// 1=Weak (op=0197 multi-room broadcast).
typedef struct {
    int32_t  group_id;
    const Aion2FunNicknameInfo* members;
    int32_t  members_count;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
    uint8_t  confidence;
    uint8_t  contains_self;
} Aion2FunPartyRosterUpdate;

// =====================================================================
// Event callback typedefs
// =====================================================================

typedef void (*Aion2FunOnDamage)        (void* ctx, const Aion2FunDamageEvent*);
typedef void (*Aion2FunOnMobHp)         (void* ctx, const Aion2FunMobHpUpdate*);
typedef void (*Aion2FunOnEncounter)     (void* ctx, const Aion2FunEncounterAnnouncement*);
typedef void (*Aion2FunOnCombatBoundary)(void* ctx, const Aion2FunCombatBoundary*);
typedef void (*Aion2FunOnNickname)      (void* ctx, const Aion2FunNicknameInfo*);
typedef void (*Aion2FunOnSummonSpawn)   (void* ctx, const Aion2FunSummonSpawnInfo*);
typedef void (*Aion2FunOnCombatPower)   (void* ctx, const Aion2FunCombatPowerUpdate*);
typedef void (*Aion2FunOnPartyLeft)     (void* ctx, const Aion2FunPartyLeft*);
typedef void (*Aion2FunOnDungeon)       (void* ctx, const Aion2FunDungeonAnnouncement*);
typedef void (*Aion2FunOnPartyRoster)   (void* ctx, const Aion2FunPartyRosterUpdate*);

// =====================================================================
// PacketDispatcher
// =====================================================================
//
// Owns parsing state (LZ4 scratch buffer, malformed/lz4 counters) and
// fires typed callbacks for each parsed event. Create one instance per
// capture stream; destroy on session end.

AION2FUN_ENGINE_EXPORT Aion2FunDispatcherHandle Aion2Fun_Dispatcher_Create(void);
AION2FUN_ENGINE_EXPORT void                    Aion2Fun_Dispatcher_Destroy(Aion2FunDispatcherHandle h);

// Set per-event callbacks. nullptr = drop events of that type.
// `ctx` is forwarded to all callbacks (typically the C# managed instance
// pointer GCHandle.ToIntPtr).
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetContext         (Aion2FunDispatcherHandle h, void* ctx);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnLog           (Aion2FunDispatcherHandle h, Aion2FunOnLog cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnDamage        (Aion2FunDispatcherHandle h, Aion2FunOnDamage cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnMobHp         (Aion2FunDispatcherHandle h, Aion2FunOnMobHp cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnEncounter     (Aion2FunDispatcherHandle h, Aion2FunOnEncounter cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnCombatBoundary(Aion2FunDispatcherHandle h, Aion2FunOnCombatBoundary cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnNickname      (Aion2FunDispatcherHandle h, Aion2FunOnNickname cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnSummonSpawn   (Aion2FunDispatcherHandle h, Aion2FunOnSummonSpawn cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnCombatPower   (Aion2FunDispatcherHandle h, Aion2FunOnCombatPower cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnPartyLeft     (Aion2FunDispatcherHandle h, Aion2FunOnPartyLeft cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnDungeon       (Aion2FunDispatcherHandle h, Aion2FunOnDungeon cb);
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_SetOnPartyRoster   (Aion2FunDispatcherHandle h, Aion2FunOnPartyRoster cb);

// Feed one complete game-packet body (already varint-framed by the
// FrameAssembler). Fires whichever event callback matches the opcode.
AION2FUN_ENGINE_EXPORT void Aion2Fun_Dispatcher_Dispatch(
    Aion2FunDispatcherHandle h,
    const uint8_t* body, int32_t body_length,
    uint32_t source_ipv4,
    int64_t timestamp_ticks);

// Diagnostic counters (cumulative since instance creation).
AION2FUN_ENGINE_EXPORT int64_t Aion2Fun_Dispatcher_MalformedCount  (Aion2FunDispatcherHandle h);
AION2FUN_ENGINE_EXPORT int64_t Aion2Fun_Dispatcher_Lz4SuccessCount (Aion2FunDispatcherHandle h);
AION2FUN_ENGINE_EXPORT int64_t Aion2Fun_Dispatcher_Lz4FailureCount (Aion2FunDispatcherHandle h);

// =====================================================================
// FrameAssembler
// =====================================================================
//
// Slices an in-order TCP byte stream into game packets via varint length
// prefix. Per-flow carry-over keyed by 4-tuple flow hash. Caller pumps
// chunks via Feed; complete game packets are emitted to a callback.

typedef void (*Aion2FunOnGamePacket)(
    void* ctx,
    uint64_t flow_key,
    uint32_t source_ipv4,
    int64_t  timestamp_ticks,
    const uint8_t* body,
    int32_t  body_length);

AION2FUN_ENGINE_EXPORT Aion2FunFrameAssemblerHandle Aion2Fun_FrameAssembler_Create(void);
AION2FUN_ENGINE_EXPORT void                         Aion2Fun_FrameAssembler_Destroy(Aion2FunFrameAssemblerHandle h);

// Feed one in-order TCP chunk. Caller retains ownership of `chunk_data`.
AION2FUN_ENGINE_EXPORT void Aion2Fun_FrameAssembler_Feed(
    Aion2FunFrameAssemblerHandle h,
    uint64_t flow_key,
    uint32_t source_ipv4,
    int64_t  timestamp_ticks,
    const uint8_t* chunk_data, size_t chunk_length,
    Aion2FunOnGamePacket cb,
    void* ctx);

// Drop a flow's carryover (TCP RST/FIN cleanup).
AION2FUN_ENGINE_EXPORT void Aion2Fun_FrameAssembler_Reset(Aion2FunFrameAssemblerHandle h, uint64_t flow_key);

AION2FUN_ENGINE_EXPORT int64_t Aion2Fun_FrameAssembler_MalformedFrames(Aion2FunFrameAssemblerHandle h);
AION2FUN_ENGINE_EXPORT int32_t Aion2Fun_FrameAssembler_FlowCount      (Aion2FunFrameAssemblerHandle h);

#ifdef __cplusplus
}  // extern "C"
#endif

#endif  // AION2FUN_ENGINE_API_H
