// frame_assembler.h — TCP byte-stream → game-packet slicer.
//
// Aion 2 KR sends game packets framed by a varint length prefix. Each
// TCP segment we receive may contain:
//   - zero, one, or many complete game packets
//   - a trailing partial game packet (need more bytes)
//   - a leading partial game packet (continuation of the previous segment)
//
// FrameAssembler holds per-flow carry-over buffers (one per 4-tuple flow
// key) and emits a callback for each fully-assembled game packet.
//
// Direct port of Aion2FunDps.Protocol.FrameAssembler.cs. Differences from
// the managed reference:
//   - C# used ArrayPool<byte> for hot-path buffers; we use std::vector
//     here. ArrayPool's per-thread caching can be added later if profiling
//     shows allocator pressure (Phase 7 regression check is the place to
//     measure).
//   - Callbacks are C-style function pointers + context, suitable for
//     P/Invoke marshalling in Phase 5.
//
// Threading: a single FrameAssembler instance is single-threaded. Capture
// callers must serialize Feed() calls. Multi-flow concurrency is achieved
// by either separate instances per flow or external locking.

#ifndef AION2FUN_ENGINE_FRAME_ASSEMBLER_H
#define AION2FUN_ENGINE_FRAME_ASSEMBLER_H

#include <cstddef>
#include <cstdint>
#include <unordered_map>
#include <vector>

namespace aion2fun {

// Callback invoked for each complete game packet extracted from the
// stream. Pointer + length are valid only for the duration of the call;
// caller must copy if it needs to retain bytes.
using GamePacketCallback = void(*)(
    void* ctx,
    uint64_t flow_key,
    uint32_t source_ipv4,
    int64_t timestamp_ticks,
    const uint8_t* body,
    int32_t body_length);

class FrameAssembler {
public:
    FrameAssembler() = default;
    FrameAssembler(const FrameAssembler&) = delete;
    FrameAssembler& operator=(const FrameAssembler&) = delete;

    // Feed an in-order TCP chunk. `game_cb` fires once per complete game
    // packet extracted. Caller retains ownership of `chunk_data` — we
    // copy into per-flow carryover only when a partial frame remains.
    //
    // flow_key: 4-tuple hash (caller's choice of encoding) identifying
    //           which TCP flow this chunk belongs to. Different flows
    //           keep independent carryover state.
    void Feed(uint64_t flow_key,
              uint32_t source_ipv4,
              int64_t timestamp_ticks,
              const uint8_t* chunk_data,
              size_t chunk_length,
              GamePacketCallback game_cb,
              void* ctx);

    // Drop a flow's carryover. Call on TCP RST / FIN to release the
    // buffer; subsequent Feed() on the same key starts fresh.
    void Reset(uint64_t flow_key);

    int64_t MalformedFrames() const noexcept { return malformed_frames_; }
    int     FlowCount() const noexcept       { return static_cast<int>(carryover_.size()); }

private:
    // Per-flow carryover buffer. Empty vector = no carryover (key absent
    // from the map is the canonical "no carryover" state; empty vectors
    // shouldn't accumulate).
    std::unordered_map<uint64_t, std::vector<uint8_t>> carryover_;
    int64_t malformed_frames_ = 0;
};

}  // namespace aion2fun

#endif  // AION2FUN_ENGINE_FRAME_ASSEMBLER_H
