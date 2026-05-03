// compression_detector.h — Detects LZ4-compressed game packets.
//
// Aion 2 KR marks compressed packet bodies with `0xff 0xff` after an
// optional run of "extra-flag" bytes in the 0xf0..0xfe range. Format:
//
//   [optional 0xf0..0xfe ...] [0xff 0xff] [originLength u32 LE] [LZ4 data]
//
// Direct port of Aion2FunDps.Protocol.CompressionDetector.cs.

#ifndef AION2FUN_ENGINE_COMPRESSION_DETECTOR_H
#define AION2FUN_ENGINE_COMPRESSION_DETECTOR_H

#include <cstddef>
#include <cstdint>

namespace aion2fun {

struct CompressionProbe {
    int32_t compressed_data_offset;  // Index into body where the LZ4 section starts (origin-length prefix)
    bool    is_compressed;
};

// Probes a game packet body for the LZ4 marker. On match, returns
// is_compressed=true and compressed_data_offset pointing past the
// 0xff 0xff marker (i.e., to the [originLength] header). Otherwise
// is_compressed=false and offset is undefined.
CompressionProbe is_compressed(const uint8_t* body, size_t length) noexcept;

}  // namespace aion2fun

#endif  // AION2FUN_ENGINE_COMPRESSION_DETECTOR_H
