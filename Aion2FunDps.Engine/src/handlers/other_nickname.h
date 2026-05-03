// other_nickname.h — opcode 0x44 0x36 (other-player nickname info).
//
// Layout (TK searchOtherNickname):
//   [opcode] [userId varint] [unknown1 varint] [unknown2 varint] [skip 1 byte]
//   [search +0..+4 for valid nickname varint length 1-71] [nickname UTF-8]
//   [job byte] [search for valid server uint16 LE in 1001..1021 or 2001..2021]
//   [legion name varint length 2-24] [legion name]
//
// Direct port of OtherNicknameHandler.cs including the brute-force probe
// for nickname start position and the wide search for the server byte
// (with the 0-default semantic the C# reference fixed in 2026-05-04).

#ifndef AION2FUN_ENGINE_HANDLERS_OTHER_NICKNAME_H
#define AION2FUN_ENGINE_HANDLERS_OTHER_NICKNAME_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

bool try_parse_other_nickname(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::NicknameInfo& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
