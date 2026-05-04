// encounter_announce.cpp — implementation.

#include "encounter_announce.h"
#include "../varint.h"

namespace aion2fun::handlers {

bool try_parse_encounter_announce(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::EncounterAnnouncement& out_event) noexcept
{
    if (body == nullptr || body_length < 16) return false;
    if (body[0] != 0x01 || body[1] != 0x91) return false;

    // Header is fixed: 2 (opcode) + 2 (pad) + 5 (hdr) + 1 (count) = 10 bytes.
    // Per-boss block at offset 10 starts with a VarInt prefix (entityId-like
    // hash) then 4-byte LE mob_code.
    constexpr size_t kPerBossBase = 10;
    if (kPerBossBase >= body_length) return false;

    const auto vi = try_read_varint(body + kPerBossBase, body_length - kPerBossBase);
    if (!vi.ok) return false;

    const size_t mob_code_offset = kPerBossBase + vi.bytes_read;
    if (mob_code_offset + 4 > body_length) return false;

    const int32_t mob_code = static_cast<int32_t>(
        static_cast<uint32_t>(body[mob_code_offset])
        | (static_cast<uint32_t>(body[mob_code_offset + 1]) << 8)
        | (static_cast<uint32_t>(body[mob_code_offset + 2]) << 16)
        | (static_cast<uint32_t>(body[mob_code_offset + 3]) << 24));

    // Real mob_codes are 1M..50M per mobs.json. Reject corruption /
    // wrong-offset reads.
    if (mob_code < 1'000'000 || mob_code > 50'000'000) return false;

    out_event.mob_code        = mob_code;
    out_event.timestamp_ticks = timestamp_ticks;
    out_event.source_ipv4     = source_ipv4;
    return true;
}

}  // namespace aion2fun::handlers
