// lz4_decompress.h — LZ4 block-format decompressor for Aion 2 KR packets.
//
// Aion 2's compressed packet structure (after the 0xff 0xff marker is
// consumed by the frame assembler):
//
//   [originLength: uint32 LE, 4 bytes] [LZ4-compressed body...]
//
// LZ4 block format requires the caller to know the exact decompressed
// size up-front, which is why the protocol prefixes it. We reject
// implausible originLengths up-front to avoid accidentally allocating
// gigabyte buffers when a corrupt frame leaks into this path.
//
// Direct port of Aion2FunDps.Protocol.Lz4Decompressor.cs. Difference:
// the C# version used ArrayPool<byte> to recycle output buffers; here we
// take the output buffer from the caller (zero-copy, no pool needed —
// the caller can manage its own pool with whatever strategy fits).

#ifndef AION2FUN_ENGINE_LZ4_DECOMPRESS_H
#define AION2FUN_ENGINE_LZ4_DECOMPRESS_H

#include <cstddef>
#include <cstdint>

namespace aion2fun {

// Hard cap on origin length — anything bigger is treated as a corrupt
// frame and rejected. 4 MB matches the C# reference; in practice game
// packets are much smaller (~10 KB), so this is a defense-in-depth
// against malformed prefixes that would otherwise ask us to allocate
// many megabytes per packet.
inline constexpr int32_t kMaxOriginLength = 4 * 1024 * 1024;

struct DecompressResult {
    int32_t bytes_written;  // Actual decoded bytes (== origin_length on success)
    bool    ok;
};

// Decompresses `compressed` (length `compressed_len`) into `out_buffer`
// (capacity `origin_length`). Returns ok=false when:
//   - origin_length is out of plausible range
//   - LZ4 decoder fails (truncated / corrupt block)
//   - actual decoded byte count != origin_length (impossible per protocol)
//
// out_buffer must be at least origin_length bytes. Caller owns the
// buffer; we never store the pointer.
DecompressResult try_decompress(
    const uint8_t* compressed, size_t compressed_len,
    int32_t origin_length,
    uint8_t* out_buffer) noexcept;

}  // namespace aion2fun

#endif  // AION2FUN_ENGINE_LZ4_DECOMPRESS_H
