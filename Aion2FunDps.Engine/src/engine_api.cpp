// engine_api.cpp — extern "C" thunks for the public C ABI.
//
// Pattern:
//   - Each Aion2Fun_* function does input validation + state checks, then
//     delegates to internal C++ implementation in src/internal/*.cpp.
//   - No exceptions cross the C boundary. Internal C++ may throw; we
//     catch at the boundary and surface as int32_t error codes.
//   - No globals here — engine state lives in a singleton EngineCore
//     accessed via internal helpers.
//
// Phase 0: only the lifecycle / version / log functions are real; the
// rest of the API surface (FeedPacket, callback setters) lands in
// Phases 1-5 as the wire-level pieces are ported from C#.

// AION2FUN_ENGINE_BUILDING is defined in the .vcxproj's PreprocessorDefinitions
// — don't redefine here or MSVC warns C4005 (macro redefinition).
#include "engine_api.h"

#include <atomic>
#include <cstring>

namespace {

// Module-level state. Kept minimal until Phase 1 introduces the actual
// engine internals (LZ4 buffer pools, frame state, etc.). Atomic so
// re-entrant Shutdown / Init from misbehaving callers doesn't corrupt.
std::atomic<bool> g_initialized{false};
Aion2FunOnLog g_log_cb = nullptr;
void* g_log_ctx = nullptr;

// Internal log helper — used by the engine's own paths once they exist.
// Public log surface stays as the callback registered via
// Aion2Fun_SetLogCallback.
void engine_log(int level, const char* msg) {
    auto cb = g_log_cb;
    if (cb && msg) cb(g_log_ctx, level, msg);
}

}  // anonymous namespace

extern "C" {

uint32_t Aion2Fun_GetVersion(void) {
    // 0.1.0 — bumped per release, matched against C# constant on init.
    return 0u * 10000u + 1u * 100u + 0u;
}

int32_t Aion2Fun_Init(void) {
    bool expected = false;
    if (!g_initialized.compare_exchange_strong(expected, true)) {
        // Already initialized. Idempotent return so a re-init from a
        // bug-recovery path doesn't crash the host.
        return 0;
    }
    engine_log(0, "Aion2FunDps.Engine initialized");
    return 0;
}

void Aion2Fun_Shutdown(void) {
    bool expected = true;
    if (!g_initialized.compare_exchange_strong(expected, false)) {
        // Not initialized; nothing to do. Safe to call multiple times.
        return;
    }
    engine_log(0, "Aion2FunDps.Engine shutdown");
    g_log_cb = nullptr;
    g_log_ctx = nullptr;
}

void Aion2Fun_SetLogCallback(Aion2FunOnLog cb, void* ctx) {
    g_log_cb = cb;
    g_log_ctx = ctx;
}

}  // extern "C"
