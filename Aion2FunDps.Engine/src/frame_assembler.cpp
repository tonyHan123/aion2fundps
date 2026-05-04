// frame_assembler.cpp — implementation.
//
// Mirrors Aion2FunDps.Protocol.FrameAssembler.Feed line-for-line; refer to
// the C# original for the framing-quirk commentary that we don't repeat
// here. The "-4" subtraction in realLength is the Aion 2 protocol artifact
// documented at FrameAssembler.cs:55 (TK-open-public reference).

#include "frame_assembler.h"
#include "varint.h"

#include <cstring>

namespace aion2fun {

void FrameAssembler::Feed(
    uint64_t flow_key,
    uint32_t source_ipv4,
    int64_t timestamp_ticks,
    const uint8_t* chunk_data,
    size_t chunk_length,
    GamePacketCallback game_cb,
    void* ctx)
{
    if (chunk_data == nullptr || chunk_length == 0) return;
    if (game_cb == nullptr) return;

    // Combine prior carryover with the new chunk into a single contiguous
    // buffer for slicing. We pop the entry from the map up-front; if there
    // are leftover bytes after slicing, we re-insert.
    std::vector<uint8_t> combined;
    auto it = carryover_.find(flow_key);
    if (it != carryover_.end()) {
        combined = std::move(it->second);
        carryover_.erase(it);
    }
    const size_t prior_len = combined.size();
    combined.resize(prior_len + chunk_length);
    std::memcpy(combined.data() + prior_len, chunk_data, chunk_length);

    const size_t combined_len = combined.size();
    size_t offset = 0;

    while (offset < combined_len) {
        const VarIntResult vi = try_read_varint(combined.data() + offset,
                                                combined_len - offset);
        if (!vi.ok) break;  // either incomplete (need more bytes) or corrupt prefix

        // Aion 2 framing: realLength = varintValue + varintBytes - 4
        // The "-4" is a protocol artifact (size value includes 4 bytes the
        // assembled-packet body excludes). Reference: TK-open-public
        // StreamAssembler.kt and our C# FrameAssembler.cs:55.
        const int32_t real_length = vi.value + vi.bytes_read - 4;

        if (real_length <= vi.bytes_read) {
            // Length value too small to contain a body — corrupt frame.
            // Skip everything in the current combined buffer to avoid
            // accumulating malformed bytes (matches C# `offset = combinedLen`).
            ++malformed_frames_;
            offset = combined_len;
            break;
        }

        if (combined_len - offset < static_cast<size_t>(real_length)) {
            // Body incomplete — wait for more TCP data.
            break;
        }

        const int32_t body_length = real_length - vi.bytes_read;
        const uint8_t* body_ptr = combined.data() + offset + vi.bytes_read;

        // Fire callback. Pointer is valid only for the duration of the
        // call; the next loop iteration may invalidate it (combined buffer
        // could be moved on resize, though it shouldn't here since we
        // don't append after this point).
        game_cb(ctx, flow_key, source_ipv4, timestamp_ticks,
                body_ptr, body_length);

        offset += real_length;
    }

    // Save remaining bytes as carryover for the next Feed() on this flow.
    if (offset < combined_len) {
        const size_t remaining = combined_len - offset;
        std::vector<uint8_t> next_carry(remaining);
        std::memcpy(next_carry.data(), combined.data() + offset, remaining);
        carryover_.emplace(flow_key, std::move(next_carry));
    }
}

void FrameAssembler::Reset(uint64_t flow_key) {
    carryover_.erase(flow_key);
}

}  // namespace aion2fun
