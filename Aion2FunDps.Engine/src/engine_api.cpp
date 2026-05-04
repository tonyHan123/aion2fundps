// engine_api.cpp — extern "C" thunks for the public C ABI.
//
// Pattern:
//   - Each Aion2Fun_* function does input validation, then forwards to
//     the internal C++ class methods (PacketDispatcher / FrameAssembler).
//   - Handle params (void*) are reinterpret_cast back to typed pointers.
//   - No exceptions cross the C boundary. Internal C++ methods are
//     marked noexcept where possible; the rest are wrapped in try/catch
//     at the boundary if needed.
//   - Layout-compat between Aion2FunXxx PODs (engine_api.h) and
//     events::Xxx (events.h) is enforced via static_assert at the seam
//     so divergence breaks the build instead of silently corrupting
//     callbacks.

// AION2FUN_ENGINE_BUILDING is defined in the .vcxproj's PreprocessorDefinitions
#include "engine_api.h"

#include "events.h"
#include "packet_dispatcher.h"
#include "frame_assembler.h"

#include <atomic>
#include <cstring>
#include <new>

// ---------------------------------------------------------------------
// Layout compatibility static_asserts
// ---------------------------------------------------------------------
// If anyone tweaks events.h or engine_api.h fields, these break the
// build immediately. Catches ABI drift before it produces silently
// wrong callback data on the C# side.

static_assert(sizeof(Aion2FunDamageEvent) == sizeof(aion2fun::events::DamageEvent),
              "DamageEvent ABI mismatch");
static_assert(sizeof(Aion2FunMobHpUpdate) == sizeof(aion2fun::events::MobHpUpdate),
              "MobHpUpdate ABI mismatch");
static_assert(sizeof(Aion2FunEncounterAnnouncement) == sizeof(aion2fun::events::EncounterAnnouncement),
              "EncounterAnnouncement ABI mismatch");
static_assert(sizeof(Aion2FunCombatBoundary) == sizeof(aion2fun::events::CombatBoundary),
              "CombatBoundary ABI mismatch");
static_assert(sizeof(Aion2FunNicknameInfo) == sizeof(aion2fun::events::NicknameInfo),
              "NicknameInfo ABI mismatch");
static_assert(sizeof(Aion2FunSummonSpawnInfo) == sizeof(aion2fun::events::SummonSpawnInfo),
              "SummonSpawnInfo ABI mismatch");
static_assert(sizeof(Aion2FunCombatPowerUpdate) == sizeof(aion2fun::events::CombatPowerUpdate),
              "CombatPowerUpdate ABI mismatch");
static_assert(sizeof(Aion2FunPartyLeft) == sizeof(aion2fun::events::PartyLeft),
              "PartyLeft ABI mismatch");
static_assert(sizeof(Aion2FunDungeonAnnouncement) == sizeof(aion2fun::events::DungeonAnnouncement),
              "DungeonAnnouncement ABI mismatch");
static_assert(sizeof(Aion2FunPartyRosterUpdate) == sizeof(aion2fun::events::PartyRosterUpdate),
              "PartyRosterUpdate ABI mismatch");

// ---------------------------------------------------------------------
// Module-level state (lifecycle)
// ---------------------------------------------------------------------
namespace {

std::atomic<bool> g_initialized{false};
Aion2FunOnLog g_log_cb = nullptr;
void* g_log_ctx = nullptr;

void engine_log(int level, const char* msg) {
    auto cb = g_log_cb;
    if (cb && msg) cb(g_log_ctx, level, msg);
}

// Aliased dispatcher state. We bundle the C++ PacketDispatcher with its
// callback table because the dispatcher itself takes the callbacks by
// reference per-Dispatch call, but our C ABI binds them stickily via
// SetOnXxx. Wrapping keeps the binding outside the dispatcher class.
struct DispatcherState {
    aion2fun::PacketDispatcher core;
    aion2fun::DispatcherCallbacks cbs;
};

// Helper: cast handle → typed object.
inline DispatcherState* as_disp(Aion2FunDispatcherHandle h) {
    return reinterpret_cast<DispatcherState*>(h);
}
inline aion2fun::FrameAssembler* as_fa(Aion2FunFrameAssemblerHandle h) {
    return reinterpret_cast<aion2fun::FrameAssembler*>(h);
}

}  // anonymous namespace

// ---------------------------------------------------------------------
// extern "C" surface
// ---------------------------------------------------------------------

extern "C" {

// =========================================================================
// Lifecycle
// =========================================================================

uint32_t Aion2Fun_GetVersion(void) {
    return 0u * 10000u + 1u * 100u + 0u;  // 0.1.0
}

int32_t Aion2Fun_Init(void) {
    bool expected = false;
    if (!g_initialized.compare_exchange_strong(expected, true)) return 0;
    engine_log(0, "Aion2FunDps.Engine initialized");
    return 0;
}

void Aion2Fun_Shutdown(void) {
    bool expected = true;
    if (!g_initialized.compare_exchange_strong(expected, false)) return;
    engine_log(0, "Aion2FunDps.Engine shutdown");
    g_log_cb = nullptr;
    g_log_ctx = nullptr;
}

void Aion2Fun_SetLogCallback(Aion2FunOnLog cb, void* ctx) {
    g_log_cb = cb;
    g_log_ctx = ctx;
}

// =========================================================================
// PacketDispatcher
// =========================================================================

Aion2FunDispatcherHandle Aion2Fun_Dispatcher_Create(void) {
    auto* state = new (std::nothrow) DispatcherState();
    return reinterpret_cast<Aion2FunDispatcherHandle>(state);
}

void Aion2Fun_Dispatcher_Destroy(Aion2FunDispatcherHandle h) {
    delete as_disp(h);
}

void Aion2Fun_Dispatcher_SetContext(Aion2FunDispatcherHandle h, void* ctx) {
    if (auto* d = as_disp(h)) d->cbs.ctx = ctx;
}

// Each setter casts the function pointer to the matching C++ type. Both
// share the same calling convention + signature shape on x64 (cdecl with
// pointer-sized args), so the cast is layout-correct.
void Aion2Fun_Dispatcher_SetOnLog(Aion2FunDispatcherHandle h, Aion2FunOnLog cb) {
    if (auto* d = as_disp(h)) d->cbs.on_log =
        reinterpret_cast<aion2fun::DispatcherLogCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnDamage(Aion2FunDispatcherHandle h, Aion2FunOnDamage cb) {
    if (auto* d = as_disp(h)) d->cbs.on_damage =
        reinterpret_cast<aion2fun::DamageCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnMobHp(Aion2FunDispatcherHandle h, Aion2FunOnMobHp cb) {
    if (auto* d = as_disp(h)) d->cbs.on_mob_hp =
        reinterpret_cast<aion2fun::MobHpCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnEncounter(Aion2FunDispatcherHandle h, Aion2FunOnEncounter cb) {
    if (auto* d = as_disp(h)) d->cbs.on_encounter =
        reinterpret_cast<aion2fun::EncounterCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnCombatBoundary(Aion2FunDispatcherHandle h, Aion2FunOnCombatBoundary cb) {
    if (auto* d = as_disp(h)) d->cbs.on_combat_boundary =
        reinterpret_cast<aion2fun::CombatBoundaryCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnNickname(Aion2FunDispatcherHandle h, Aion2FunOnNickname cb) {
    if (auto* d = as_disp(h)) d->cbs.on_nickname =
        reinterpret_cast<aion2fun::NicknameCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnSummonSpawn(Aion2FunDispatcherHandle h, Aion2FunOnSummonSpawn cb) {
    if (auto* d = as_disp(h)) d->cbs.on_summon_spawn =
        reinterpret_cast<aion2fun::SummonSpawnCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnCombatPower(Aion2FunDispatcherHandle h, Aion2FunOnCombatPower cb) {
    if (auto* d = as_disp(h)) d->cbs.on_combat_power =
        reinterpret_cast<aion2fun::CombatPowerCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnPartyLeft(Aion2FunDispatcherHandle h, Aion2FunOnPartyLeft cb) {
    if (auto* d = as_disp(h)) d->cbs.on_party_left =
        reinterpret_cast<aion2fun::PartyLeftCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnDungeon(Aion2FunDispatcherHandle h, Aion2FunOnDungeon cb) {
    if (auto* d = as_disp(h)) d->cbs.on_dungeon =
        reinterpret_cast<aion2fun::DungeonCallback>(cb);
}
void Aion2Fun_Dispatcher_SetOnPartyRoster(Aion2FunDispatcherHandle h, Aion2FunOnPartyRoster cb) {
    if (auto* d = as_disp(h)) d->cbs.on_party_roster =
        reinterpret_cast<aion2fun::PartyRosterCallback>(cb);
}

void Aion2Fun_Dispatcher_Dispatch(
    Aion2FunDispatcherHandle h,
    const uint8_t* body, int32_t body_length,
    uint32_t source_ipv4, int64_t timestamp_ticks)
{
    if (auto* d = as_disp(h)) {
        d->core.Dispatch(body, body_length, source_ipv4, timestamp_ticks, d->cbs);
    }
}

int64_t Aion2Fun_Dispatcher_MalformedCount(Aion2FunDispatcherHandle h) {
    return as_disp(h) ? as_disp(h)->core.MalformedCount() : 0;
}
int64_t Aion2Fun_Dispatcher_Lz4SuccessCount(Aion2FunDispatcherHandle h) {
    return as_disp(h) ? as_disp(h)->core.Lz4SuccessCount() : 0;
}
int64_t Aion2Fun_Dispatcher_Lz4FailureCount(Aion2FunDispatcherHandle h) {
    return as_disp(h) ? as_disp(h)->core.Lz4FailureCount() : 0;
}

// =========================================================================
// FrameAssembler
// =========================================================================

Aion2FunFrameAssemblerHandle Aion2Fun_FrameAssembler_Create(void) {
    auto* fa = new (std::nothrow) aion2fun::FrameAssembler();
    return reinterpret_cast<Aion2FunFrameAssemblerHandle>(fa);
}

void Aion2Fun_FrameAssembler_Destroy(Aion2FunFrameAssemblerHandle h) {
    delete as_fa(h);
}

void Aion2Fun_FrameAssembler_Feed(
    Aion2FunFrameAssemblerHandle h,
    uint64_t flow_key,
    uint32_t source_ipv4,
    int64_t timestamp_ticks,
    const uint8_t* chunk_data, size_t chunk_length,
    Aion2FunOnGamePacket cb,
    void* ctx)
{
    if (auto* fa = as_fa(h)) {
        fa->Feed(flow_key, source_ipv4, timestamp_ticks,
                 chunk_data, chunk_length,
                 reinterpret_cast<aion2fun::GamePacketCallback>(cb), ctx);
    }
}

void Aion2Fun_FrameAssembler_Reset(Aion2FunFrameAssemblerHandle h, uint64_t flow_key) {
    if (auto* fa = as_fa(h)) fa->Reset(flow_key);
}

int64_t Aion2Fun_FrameAssembler_MalformedFrames(Aion2FunFrameAssemblerHandle h) {
    return as_fa(h) ? as_fa(h)->MalformedFrames() : 0;
}
int32_t Aion2Fun_FrameAssembler_FlowCount(Aion2FunFrameAssemblerHandle h) {
    return as_fa(h) ? as_fa(h)->FlowCount() : 0;
}

}  // extern "C"
