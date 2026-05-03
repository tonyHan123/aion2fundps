// combat_power.cpp — implementation.

#include "combat_power.h"
#include "../nickname_validator.h"

namespace aion2fun::handlers {

namespace {
inline int32_t read_i32_le(const uint8_t* p) noexcept {
    return static_cast<int32_t>(
        static_cast<uint32_t>(p[0])
        | (static_cast<uint32_t>(p[1]) << 8)
        | (static_cast<uint32_t>(p[2]) << 16)
        | (static_cast<uint32_t>(p[3]) << 24));
}
}  // anonymous namespace

bool try_parse_combat_power(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::CombatPowerUpdate& out_event) noexcept
{
    if (body == nullptr || body_length < 30) return false;
    if (body[0] != 0x00 || body[1] != 0x92) return false;

    // Scan from end backward for the magic 06 00 36 anchor. Need 21
    // bytes of headroom before it for the level/zero/itemLevel/CP block.
    for (size_t num = body_length - 3; num >= 21; --num) {
        if (body[num] != 0x06 || body[num + 1] != 0x00 || body[num + 2] != 0x36) {
            continue;
        }

        // 5-byte zero pad immediately preceding the magic.
        bool zeros = true;
        for (size_t j = num - 5; j < num; ++j) {
            if (body[j] != 0) { zeros = false; break; }
        }
        if (!zeros) continue;

        const int32_t cp = read_i32_le(body + num - 9);
        if (cp < 10'000 || cp > 999'999) continue;

        const int32_t item_level = read_i32_le(body + num - 13);
        if (item_level < 1000 || item_level > 5000) continue;

        const int32_t zero_field = read_i32_le(body + num - 17);
        if (zero_field != 0) continue;

        const int32_t level = read_i32_le(body + num - 21);
        if (level < 1 || level > 55) continue;

        // Found a valid stats trailer at `num`. Now scan the preceding
        // bytes for [server 2 LE][nameLen 1][nickname N] that ends
        // before the trailer.
        const size_t name_scan_limit = num - 21;
        for (size_t k = 0; k + 3 < name_scan_limit; ++k) {
            const int32_t server = static_cast<int32_t>(
                static_cast<uint32_t>(body[k])
                | (static_cast<uint32_t>(body[k + 1]) << 8));
            if (server < 1001 || server > 2021) continue;

            const int32_t name_len = body[k + 2];
            if (name_len < 3 || name_len > 48) continue;
            if (k + 3 + static_cast<size_t>(name_len) > name_scan_limit) continue;

            if (!is_valid_nickname(body + k + 3, static_cast<size_t>(name_len))) continue;

            out_event.nickname         = reinterpret_cast<const char*>(body + k + 3);
            out_event.nickname_len     = name_len;
            out_event.server_id        = server;
            out_event.combat_power     = cp;
            out_event.timestamp_ticks  = timestamp_ticks;
            out_event.source_ipv4      = source_ipv4;
            return true;
        }
    }
    return false;
}

}  // namespace aion2fun::handlers
