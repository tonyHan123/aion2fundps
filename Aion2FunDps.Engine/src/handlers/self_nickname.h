// self_nickname.h — opcode 0x33 0x36 (self character nickname info).
//
// Layout: [opcode] [userId varint] [10-byte search for 0x07 spliter]
//         [nameLength varint] [name UTF-8] [server uint16 LE] [job byte]
//         [...skill/buff slot template, 36 bytes...] [combatPower uint32 LE @ len-40]
//
// Direct port of SelfNicknameHandler.cs. The C# version maintained
// LastSelfUserId / LastSelfNickname static fields used by other handlers
// — in C++ that state moves to the C# aggregator (NicknameRegistry) which
// already tracks self via the IsSelf=true flag on emitted events. So
// no static state is needed here.

#ifndef AION2FUN_ENGINE_HANDLERS_SELF_NICKNAME_H
#define AION2FUN_ENGINE_HANDLERS_SELF_NICKNAME_H

#include <cstddef>
#include <cstdint>
#include "../events.h"

namespace aion2fun::handlers {

// Output event's `nickname` pointer aliases into `body`; valid only for
// the duration of the dispatcher callback that emits this event.
bool try_parse_self_nickname(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    events::NicknameInfo& out_event) noexcept;

}  // namespace aion2fun::handlers

#endif
