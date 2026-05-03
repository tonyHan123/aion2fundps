// packet_dispatcher.cpp — implementation.
//
// Three-method skeleton matches C# PacketDispatcher.cs (Dispatch /
// DispatchCompressed / DispatchOpcode). Phase 3 wires the structure;
// per-opcode handlers will populate DispatchOpcode with parsing logic
// in Phase 4 (currently it just emits an "opcode received" callback
// unchanged across all opcodes).

#include "packet_dispatcher.h"
#include "compression_detector.h"
#include "lz4_decompress.h"
#include "varint.h"

namespace aion2fun {

namespace {
// Read a 4-byte little-endian uint32. Used to parse the origin-length
// header that prefixes the LZ4 compressed section.
inline int32_t read_u32_le(const uint8_t* p) noexcept {
    return static_cast<int32_t>(
        static_cast<uint32_t>(p[0])
        | (static_cast<uint32_t>(p[1]) << 8)
        | (static_cast<uint32_t>(p[2]) << 16)
        | (static_cast<uint32_t>(p[3]) << 24));
}
}  // anonymous namespace

void PacketDispatcher::Dispatch(
    const uint8_t* body, int32_t body_length,
    uint32_t source_ipv4, int64_t timestamp_ticks,
    const DispatcherCallbacks& cbs)
{
    if (body == nullptr || body_length <= 0) return;

    const auto probe = is_compressed(body, static_cast<size_t>(body_length));
    if (probe.is_compressed) {
        const int32_t comp_offset = probe.compressed_data_offset;
        DispatchCompressed(body + comp_offset, body_length - comp_offset,
                           source_ipv4, timestamp_ticks, cbs);
        return;
    }

    DispatchOpcode(body, body_length, source_ipv4, timestamp_ticks, cbs);
}

void PacketDispatcher::DispatchCompressed(
    const uint8_t* compressed, int32_t compressed_len,
    uint32_t source_ipv4, int64_t timestamp_ticks,
    const DispatcherCallbacks& cbs)
{
    // Layout (after the 0xff 0xff marker is consumed by Dispatch):
    //   [originLength: u32 LE, 4 bytes] [LZ4 data ...]
    if (compressed_len < 5) return;

    const int32_t origin_length = read_u32_le(compressed);

    // Pre-size the scratch buffer; LZ4 caps already validated by
    // try_decompress.
    if (origin_length <= 0 || origin_length > kMaxOriginLength) {
        ++lz4_failure_count_;
        return;
    }
    if (decompress_scratch_.size() < static_cast<size_t>(origin_length)) {
        decompress_scratch_.resize(origin_length);
    }

    const auto dr = try_decompress(
        compressed + 4, compressed_len - 4,
        origin_length,
        decompress_scratch_.data());
    if (!dr.ok) {
        ++lz4_failure_count_;
        return;
    }
    ++lz4_success_count_;

    // Decompressed buffer holds a stream of varint-framed inner packets.
    // varint == 0 is a "skip 1 byte" sentinel per the C# reference (TK
    // StreamProcessor: encountered in some bulk packets where a leading
    // null byte separates entries from the next chunk).
    const uint8_t* span = decompress_scratch_.data();
    const int32_t span_len = origin_length;
    int32_t offset = 0;

    while (offset < span_len) {
        const auto vi = try_read_varint(span + offset, static_cast<size_t>(span_len - offset));
        if (!vi.ok) break;

        if (vi.value == 0) {
            offset += 1;
            continue;
        }

        const int32_t real_len = vi.value + vi.bytes_read - 4;
        if (real_len <= vi.bytes_read || (span_len - offset) < real_len) break;

        const uint8_t* inner_body = span + offset + vi.bytes_read;
        const int32_t  inner_len  = real_len - vi.bytes_read;
        DispatchOpcode(inner_body, inner_len, source_ipv4, timestamp_ticks, cbs);

        offset += real_len;
    }
}

void PacketDispatcher::DispatchOpcode(
    const uint8_t* body, int32_t body_length,
    uint32_t source_ipv4, int64_t timestamp_ticks,
    const DispatcherCallbacks& cbs)
{
    if (body == nullptr || body_length < 2) {
        ++malformed_count_;
        return;
    }

    const uint8_t op0 = body[0];
    const uint8_t op1 = body[1];

    // Phase 3 routing: emit generic "opcode received" callback. Phase 4
    // will replace this with per-opcode handler dispatch (parse body,
    // populate event struct, call typed callback).
    //
    // Why we don't branch per opcode here yet: until handlers exist the
    // branches would all do the same thing (call on_opcode). Cleaner to
    // start with a single emission point and split during Phase 4 when
    // each branch needs its own parsing logic + event-type callback.
    if (cbs.on_opcode != nullptr) {
        cbs.on_opcode(cbs.ctx, op0, op1, source_ipv4, timestamp_ticks,
                      body, body_length);
    }
}

}  // namespace aion2fun
