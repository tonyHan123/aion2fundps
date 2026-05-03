// events.h — POD event structures emitted by the dispatcher / handlers.
//
// One C++ struct per IGameEvent record in Aion2FunDps.Core/Models/. Field
// types are sized integers + raw `const char*` for strings (with explicit
// length) so the structures cross the C ABI boundary cleanly when Phase 5
// adds the P/Invoke layer.
//
// Strings: pointer + length, UTF-8, valid only for the duration of the
// callback in which they were emitted. Callers must copy if they need to
// retain bytes (matches A2Power's PacketEngine convention).
//
// Bool members are uint8_t so the struct layout is portable across
// compilers without C++ bool-size assumptions surfacing at the FFI seam.

#ifndef AION2FUN_ENGINE_EVENTS_H
#define AION2FUN_ENGINE_EVENTS_H

#include <cstdint>

namespace aion2fun::events {

// SpecialDamageFlags bitmask — mirror of C# enum in DamageEvent.cs.
// Bytes match the wire format directly (no host re-encoding); the
// aggregator's flag-test code is identical between managed and native.
enum DamageFlags : uint8_t {
    DF_None     = 0x00,
    DF_Back     = 0x01,
    DF_Unknown1 = 0x02,
    DF_Parry    = 0x04,
    DF_Perfect  = 0x08,
    DF_Double   = 0x10,
    DF_Endure   = 0x20,
    DF_Multi    = 0x40,
    DF_Block    = 0x80,
};

// RosterConfidence — strong (op=0297 / 6ae2) vs weak (op=0197).
enum RosterConfidence : uint8_t {
    RC_Strong = 0,
    RC_Weak   = 1,
};

struct DamageEvent {
    int32_t  actor_id;
    int32_t  target_id;
    int32_t  damage;
    uint32_t skill_code;
    int32_t  type;             // 3 = critical (matches C# IsCritical)
    uint8_t  specials;         // DamageFlags bitmask
    int32_t  loop;             // multi-hit count
    uint8_t  is_dot;           // bool: 1 if the damage came from a DotHandler emit
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
};

struct MobHpUpdate {
    int32_t  mob_id;
    int64_t  current_hp;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
};

struct EncounterAnnouncement {
    int32_t  mob_code;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
};

struct CombatBoundary {
    int32_t  mob_id;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
};

// NicknameInfo — emitted from SELF_NICK / OTHER_NICK / op=0197 / op=0297
// / op=6ae2. Same struct, different is_party_member / is_roster_start
// flags depending on which handler emitted it (see C# reference in
// NicknameInfo.cs for semantic distinctions).
struct NicknameInfo {
    int32_t  user_id;
    const char* nickname;          // UTF-8, NOT null-terminated
    int32_t  nickname_len;
    uint8_t  is_self;
    int32_t  server;
    int32_t  job;
    int32_t  combat_power;         // 0 = not extractable
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
    uint8_t  is_party_member;
    uint8_t  is_roster_start;
    int32_t  room_id;              // 0 if not from a room broadcast
};

struct SummonSpawnInfo {
    int32_t  summon_id;
    int32_t  owner_id;             // 0 if not extractable
    int32_t  mob_code;             // -1 sentinel if not present
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
    const char* owner_name;        // nullable; may be null when not extractable
    int32_t  owner_name_len;
};

struct CombatPowerUpdate {
    const char* nickname;          // UTF-8, NOT null-terminated
    int32_t  nickname_len;
    int32_t  server_id;
    int32_t  combat_power;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
};

struct PartyLeft {
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
};

struct DungeonAnnouncement {
    int32_t  dungeon_id;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
};

// PartyRosterUpdate carries an array of members. The array memory is
// owned by the caller of the callback (lives only for the duration of
// the callback). C# side must marshal each NicknameInfo into managed
// objects before returning.
struct PartyRosterUpdate {
    int32_t  group_id;
    const NicknameInfo* members;
    int32_t  members_count;
    int64_t  timestamp_ticks;
    uint32_t source_ipv4;
    uint8_t  confidence;           // RosterConfidence enum
    uint8_t  contains_self;
};

}  // namespace aion2fun::events

#endif  // AION2FUN_ENGINE_EVENTS_H
