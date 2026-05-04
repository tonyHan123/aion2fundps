// varint.cpp — implementation of try_read_varint.
//
// Mirrors the C# reference at Aion2FunDps.Protocol.FrameAssembler line-for-line
// (see managed reference for original commentary on edge cases).

#include "varint.h"

namespace aion2fun {

VarIntResult try_read_varint(const uint8_t* data, size_t len) noexcept {
    if (data == nullptr || len == 0) {
        return {0, 0, false};
    }

    int32_t value = 0;
    int shift = 0;
    const size_t cap = (len < 5) ? len : 5;  // 5-byte hard limit

    for (size_t i = 0; i < cap; ++i) {
        const uint8_t b = data[i];

        // Overflow guard: on the 5th byte, only the bottom 4 bits are
        // valid payload. If the upper 4 bits are set, the value would
        // shift into the sign bit and wrap to negative — corrupt
        // prefix, reject the whole varint.
        if (i == 4 && (b & 0xF0) != 0) {
            return {0, 0, false};
        }

        value |= static_cast<int32_t>(b & 0x7F) << shift;

        // Continuation bit clear → end of varint, success.
        if ((b & 0x80) == 0) {
            // Final paranoia check: positive overflow could happen even
            // before the 5th-byte gate above on some input shapes; reject
            // any negative final value just in case.
            if (value < 0) return {0, 0, false};
            return {value, static_cast<int>(i) + 1, true};
        }

        shift += 7;
    }

    // Ran out of bytes (or exhausted 5 without seeing a stop bit) —
    // either truncated input (caller should wait for more) or a malformed
    // prefix. Either way, not parseable as a complete varint right now.
    return {0, 0, false};
}

}  // namespace aion2fun
