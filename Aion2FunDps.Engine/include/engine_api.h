// engine_api.h — Public C ABI surface of Aion2FunDps.Engine.dll
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
// caller is valid only for the lifetime of the callback in which it was
// emitted. The engine reuses internal buffers between events; the C# side
// must marshal the bytes/string immediately and not retain the pointer.
//
// Threading: all engine functions are designed to be called from a single
// capture thread. Callbacks fire on that same thread. C# is responsible
// for any cross-thread marshalling to the UI dispatcher.
//
// Reference: design parallels A2Viewer's PacketEngineInterop.cs P/Invoke
// shape (tools/a2viewer-src/A2Viewer.Packet/PacketEngineInterop.cs),
// validated against ours during the Phase 7 regression check.

#ifndef AION2FUN_ENGINE_API_H
#define AION2FUN_ENGINE_API_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Visibility macro. AION2FUN_ENGINE_EXPORT marks a symbol for export from
// the DLL. We use __declspec(dllexport) when building the DLL itself
// (AION2FUN_ENGINE_BUILDING is defined by the .vcxproj) and the empty
// fallback when consumed (header-only consumers like docs / tests).
#if defined(AION2FUN_ENGINE_BUILDING)
#  define AION2FUN_ENGINE_EXPORT __declspec(dllexport)
#else
#  define AION2FUN_ENGINE_EXPORT
#endif

// Callbacks: opaque user-data pointer (`ctx`) passed back to C# so it
// can route events to the right managed instance without static state.
typedef void (*Aion2FunOnLog)(void* ctx, int level, const char* message);

// Returns engine version (semver-ish: major*10000 + minor*100 + patch).
// Used for sanity-checking that the C# side is paired with a compatible
// DLL build at startup. Match against engine_version.cs constant.
AION2FUN_ENGINE_EXPORT uint32_t Aion2Fun_GetVersion(void);

// Initializes engine state. Must be called before any other API call.
// Returns 0 on success, non-zero error code on failure (out of memory,
// already initialized, etc.).
AION2FUN_ENGINE_EXPORT int32_t Aion2Fun_Init(void);

// Tears down engine state, frees buffers, releases callbacks. Safe to
// call from process-exit; idempotent (multiple calls are no-ops after
// the first).
AION2FUN_ENGINE_EXPORT void Aion2Fun_Shutdown(void);

// Diagnostic logging hook. C# attaches its logger here so any internal
// engine warnings (parse failures, buffer exhaustion) surface in the
// same diagnostic log files as the rest of the meter. Pass null to
// disable.
AION2FUN_ENGINE_EXPORT void Aion2Fun_SetLogCallback(Aion2FunOnLog cb, void* ctx);

#ifdef __cplusplus
}  // extern "C"
#endif

#endif  // AION2FUN_ENGINE_API_H
