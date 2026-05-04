// compression_detector.cpp — implementation.

#include "compression_detector.h"

namespace aion2fun {

CompressionProbe is_compressed(const uint8_t* body, size_t length) noexcept {
    CompressionProbe probe{0, false};
    if (body == nullptr || length < 2) return probe;

    // Skip "extra-flag" bytes in the 0xf0..0xfe range. Some packets
    // carry one or more of these before the actual marker pair; the
    // semantics of the flags themselves aren't relevant to compression
    // — we just step past them.
    size_t i = 0;
    while (i < length && body[i] >= 0xf0 && body[i] <= 0xfe) {
        ++i;
    }

    // Look for the 0xff 0xff pair.
    if (i + 1 < length && body[i] == 0xff && body[i + 1] == 0xff) {
        probe.compressed_data_offset = static_cast<int32_t>(i + 2);
        probe.is_compressed = true;
    }
    return probe;
}

}  // namespace aion2fun
