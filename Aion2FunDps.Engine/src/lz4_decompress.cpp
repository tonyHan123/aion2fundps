// lz4_decompress.cpp — wrapper around lz4 reference impl.

#include "lz4_decompress.h"

extern "C" {
#include "../third_party/lz4/lz4.h"
}

namespace aion2fun {

DecompressResult try_decompress(
    const uint8_t* compressed, size_t compressed_len,
    int32_t origin_length,
    uint8_t* out_buffer) noexcept
{
    // Sanity-gate origin length up-front. A corrupt prefix could ask
    // us to write 2 GB into the user's address space; refuse anything
    // outside the protocol's plausible range.
    if (origin_length <= 0 || origin_length > kMaxOriginLength) {
        return {0, false};
    }
    if (compressed == nullptr || out_buffer == nullptr) {
        return {0, false};
    }
    // LZ4_decompress_safe takes int sizes; reject inputs that would
    // overflow that signed type. Practically capped well below 2 GB
    // already by kMaxOriginLength, this is just belt + suspenders.
    if (compressed_len > static_cast<size_t>(INT32_MAX)) {
        return {0, false};
    }

    const int decoded = LZ4_decompress_safe(
        reinterpret_cast<const char*>(compressed),
        reinterpret_cast<char*>(out_buffer),
        static_cast<int>(compressed_len),
        origin_length);

    // LZ4 returns negative on error, otherwise the actual decoded byte
    // count. Protocol requires the count to match origin_length exactly;
    // any divergence means the prefix lied or the body is corrupt.
    if (decoded < 0 || decoded != origin_length) {
        return {0, false};
    }
    return {decoded, true};
}

}  // namespace aion2fun
