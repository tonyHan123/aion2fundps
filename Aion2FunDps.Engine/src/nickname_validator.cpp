// nickname_validator.cpp — UTF-8 decode + char-class check.

#include "nickname_validator.h"

namespace aion2fun {

bool is_valid_nickname(const uint8_t* data, size_t len) noexcept {
    // 30 codepoints × 3 bytes/Hangul = 90 byte cap. Reject anything larger
    // up-front so the per-codepoint loop can't run away on garbage input.
    if (data == nullptr || len == 0 || len > 90) return false;

    size_t char_count = 0;
    size_t i = 0;
    while (i < len) {
        const uint8_t b = data[i];
        uint32_t codepoint;
        size_t cp_len;

        if (b < 0x80) {
            // 1-byte ASCII.
            codepoint = b;
            cp_len = 1;
        }
        else if ((b & 0xE0) == 0xC0) {
            // 2-byte UTF-8 (U+0080..U+07FF). Used by some symbols / Latin-1
            // accented chars — the strict rule below will reject them, but
            // we still need to consume the bytes correctly to detect that.
            if (i + 1 >= len) return false;
            const uint8_t b2 = data[i + 1];
            if ((b2 & 0xC0) != 0x80) return false;
            codepoint = static_cast<uint32_t>(((b & 0x1F) << 6) | (b2 & 0x3F));
            cp_len = 2;
        }
        else if ((b & 0xF0) == 0xE0) {
            // 3-byte UTF-8 (U+0800..U+FFFF) — covers Hangul syllables.
            if (i + 2 >= len) return false;
            const uint8_t b2 = data[i + 1];
            const uint8_t b3 = data[i + 2];
            if ((b2 & 0xC0) != 0x80 || (b3 & 0xC0) != 0x80) return false;
            codepoint = static_cast<uint32_t>(
                ((b & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F));
            cp_len = 3;
        }
        else {
            // 4-byte or invalid leading byte — reject. Aion 2 nicknames don't
            // use any character outside ASCII + Hangul, so refusing 4-byte
            // sequences (CJK extension B+, emoji, etc.) is correct.
            return false;
        }

        const bool ok =
               (codepoint >= 0xAC00 && codepoint <= 0xD7A3)  // Hangul syllables
            || (codepoint >= 'a' && codepoint <= 'z')
            || (codepoint >= 'A' && codepoint <= 'Z')
            || (codepoint >= '0' && codepoint <= '9');
        if (!ok) return false;

        if (++char_count > 30) return false;
        i += cp_len;
    }

    return char_count > 0;
}

}  // namespace aion2fun
