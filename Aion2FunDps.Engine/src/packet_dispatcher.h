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
#include <vector>

namespace aion2fun {

// Phase 3 callback shape: dispatcher routes by opcode and emits a
// generic "opcode received" event. Phase 4 will replace this with
// type-specific event callbacks (DamageEvent, NicknameInfo, etc.) as
// each handler is ported. Keeping it generic for now lets the
// dispatcher be unit-testable independently.
using OpcodeCallback = void(*)(
    void* ctx,
    uint8_t op0,
    uint8_t op1,
    uint32_t source_ipv4,
    int64_t timestamp_ticks,
    const uint8_t* body,
    int32_t body_length);

// Diagnostic / parse-failure logging hook. Replaces the C# reference's
// Interlocked.Increment(_malformedCount) + various LogPacket calls.
// Phase 5 P/Invoke layer will route this to the C# diagnostic logger.
using DispatcherLogCallback = void(*)(
    void* ctx,
    int level,
    const char* message);

// Bundle of dispatcher callbacks. Caller fills in what they want; any
// nullptr entry is silently skipped (no-op dispatch).
struct DispatcherCallbacks {
    OpcodeCallback         on_opcode = nullptr;
    DispatcherLogCallback  on_log    = nullptr;
    void*                  ctx       = nullptr;  // forwarded to all callbacks
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

    int64_t malformed_count_   = 0;
    int64_t lz4_success_count_ = 0;
    int64_t lz4_failure_count_ = 0;
};

}  // namespace aion2fun

#endif  // AION2FUN_ENGINE_PACKET_DISPATCHER_H
