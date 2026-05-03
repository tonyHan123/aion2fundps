// packet_dispatcher.cpp — implementation.
//
// Three-method skeleton matches C# PacketDispatcher.cs (Dispatch /
// DispatchCompressed / DispatchOpcode). Phase 3 wires the structure;
// per-opcode handlers will populate DispatchOpcode with parsing logic
// in Phase 4 (currently it just emits an "opcode received" callback
// unchanged across all opcodes).

#include "packet_dispatcher.h"
#include "compression_detector.h"
#include "lz4_decompress.h"
#include "varint.h"

#include "handlers/combat_boundary.h"
#include "handlers/mob_hp.h"
#include "handlers/encounter_announce.h"
#include "handlers/damage.h"
#include "handlers/dot.h"
#include "handlers/self_nickname.h"
#include "handlers/other_nickname.h"
#include "handlers/summon_spawn.h"
#include "handlers/combat_power.h"
#include "handlers/party_member_status.h"
#include "handlers/party_assembly.h"

namespace aion2fun {

namespace {
// Read a 4-byte little-endian uint32. Used to parse the origin-length
// header that prefixes the LZ4 compressed section.
inline int32_t read_u32_le(const uint8_t* p) noexcept {
    return static_cast<int32_t>(
        static_cast<uint32_t>(p[0])
        | (static_cast<uint32_t>(p[1]) << 8)
        | (static_cast<uint32_t>(p[2]) << 16)
        | (static_cast<uint32_t>(p[3]) << 24));
}
}  // anonymous namespace

void PacketDispatcher::Dispatch(
    const uint8_t* body, int32_t body_length,
    uint32_t source_ipv4, int64_t timestamp_ticks,
    const DispatcherCallbacks& cbs)
{
    if (body == nullptr || body_length <= 0) return;

    const auto probe = is_compressed(body, static_cast<size_t>(body_length));
    if (probe.is_compressed) {
        const int32_t comp_offset = probe.compressed_data_offset;
        DispatchCompressed(body + comp_offset, body_length - comp_offset,
                           source_ipv4, timestamp_ticks, cbs);
        return;
    }

    DispatchOpcode(body, body_length, source_ipv4, timestamp_ticks, cbs);
}

void PacketDispatcher::DispatchCompressed(
    const uint8_t* compressed, int32_t compressed_len,
    uint32_t source_ipv4, int64_t timestamp_ticks,
    const DispatcherCallbacks& cbs)
{
    // Layout (after the 0xff 0xff marker is consumed by Dispatch):
    //   [originLength: u32 LE, 4 bytes] [LZ4 data ...]
    if (compressed_len < 5) return;

    const int32_t origin_length = read_u32_le(compressed);

    // Pre-size the scratch buffer; LZ4 caps already validated by
    // try_decompress.
    if (origin_length <= 0 || origin_length > kMaxOriginLength) {
        ++lz4_failure_count_;
        return;
    }
    if (decompress_scratch_.size() < static_cast<size_t>(origin_length)) {
        decompress_scratch_.resize(origin_length);
    }

    const auto dr = try_decompress(
        compressed + 4, compressed_len - 4,
        origin_length,
        decompress_scratch_.data());
    if (!dr.ok) {
        ++lz4_failure_count_;
        return;
    }
    ++lz4_success_count_;

    // Decompressed buffer holds a stream of varint-framed inner packets.
    // varint == 0 is a "skip 1 byte" sentinel per the C# reference (TK
    // StreamProcessor: encountered in some bulk packets where a leading
    // null byte separates entries from the next chunk).
    const uint8_t* span = decompress_scratch_.data();
    const int32_t span_len = origin_length;
    int32_t offset = 0;

    while (offset < span_len) {
        const auto vi = try_read_varint(span + offset, static_cast<size_t>(span_len - offset));
        if (!vi.ok) break;

        if (vi.value == 0) {
            offset += 1;
            continue;
        }

        const int32_t real_len = vi.value + vi.bytes_read - 4;
        if (real_len <= vi.bytes_read || (span_len - offset) < real_len) break;

        const uint8_t* inner_body = span + offset + vi.bytes_read;
        const int32_t  inner_len  = real_len - vi.bytes_read;
        DispatchOpcode(inner_body, inner_len, source_ipv4, timestamp_ticks, cbs);

        offset += real_len;
    }
}

void PacketDispatcher::DispatchOpcode(
    const uint8_t* body, int32_t body_length,
    uint32_t source_ipv4, int64_t timestamp_ticks,
    const DispatcherCallbacks& cbs)
{
    if (body == nullptr || body_length < 2) {
        ++malformed_count_;
        return;
    }

    const uint8_t op0 = body[0];
    const uint8_t op1 = body[1];
    const size_t   blen = static_cast<size_t>(body_length);

    // Per-opcode routing — each branch parses via the matching handler
    // and emits a typed callback. Order matches C# PacketDispatcher.cs
    // for ease of cross-reference. Returns after the first match
    // (opcodes are mutually exclusive).

    // 04 38 — main damage
    if (op0 == 0x04 && op1 == 0x38) {
        events::DamageEvent evt{};
        if (handlers::try_parse_damage(body, blen, timestamp_ticks, source_ipv4,
                                       /*is_dot=*/false, evt)) {
            if (cbs.on_damage) cbs.on_damage(cbs.ctx, &evt);
        } else {
            ++malformed_count_;
        }
        return;
    }

    // 05 38 — DOT damage tick
    if (op0 == 0x05 && op1 == 0x38) {
        events::DamageEvent evt{};
        if (handlers::try_parse_dot(body, blen, timestamp_ticks, source_ipv4, evt)) {
            if (cbs.on_damage) cbs.on_damage(cbs.ctx, &evt);
        } else {
            ++malformed_count_;
        }
        return;
    }

    // 00 92 — combat power broadcast (no entity_id)
    if (op0 == 0x00 && op1 == 0x92) {
        events::CombatPowerUpdate evt{};
        if (handlers::try_parse_combat_power(body, blen, timestamp_ticks, source_ipv4, evt)) {
            if (cbs.on_combat_power) cbs.on_combat_power(cbs.ctx, &evt);
        } else {
            ++malformed_count_;
        }
        return;
    }

    // 00 8d — mob HP update
    if (op0 == 0x00 && op1 == 0x8d) {
        events::MobHpUpdate evt{};
        if (handlers::try_parse_mob_hp(body, blen, timestamp_ticks, source_ipv4, evt)) {
            if (cbs.on_mob_hp) cbs.on_mob_hp(cbs.ctx, &evt);
        } else {
            ++malformed_count_;
        }
        return;
    }

    // 21 8d — combat boundary (start/end)
    if (op0 == 0x21 && op1 == 0x8d) {
        events::CombatBoundary evt{};
        if (handlers::try_parse_combat_boundary(body, blen, timestamp_ticks, source_ipv4, evt)) {
            if (cbs.on_combat_boundary) cbs.on_combat_boundary(cbs.ctx, &evt);
        } else {
            ++malformed_count_;
        }
        return;
    }

    // 33 36 — self nickname
    if (op0 == 0x33 && op1 == 0x36) {
        events::NicknameInfo evt{};
        if (handlers::try_parse_self_nickname(body, blen, timestamp_ticks, source_ipv4, evt)) {
            if (cbs.on_nickname) cbs.on_nickname(cbs.ctx, &evt);
        } else {
            ++malformed_count_;
        }
        return;
    }

    // 44 36 — other-player nickname
    if (op0 == 0x44 && op1 == 0x36) {
        events::NicknameInfo evt{};
        if (handlers::try_parse_other_nickname(body, blen, timestamp_ticks, source_ipv4, evt)) {
            if (cbs.on_nickname) cbs.on_nickname(cbs.ctx, &evt);
        } else {
            ++malformed_count_;
        }
        return;
    }

    // 40 36 — summon/entity spawn
    if (op0 == 0x40 && op1 == 0x36) {
        events::SummonSpawnInfo evt{};
        if (handlers::try_parse_summon_spawn(body, blen, timestamp_ticks, source_ipv4, evt)) {
            if (cbs.on_summon_spawn) cbs.on_summon_spawn(cbs.ctx, &evt);
        } else {
            ++malformed_count_;
        }
        return;
    }

    // 01 91 — encounter announce (boss intro)
    if (op0 == 0x01 && op1 == 0x91) {
        events::EncounterAnnouncement evt{};
        if (handlers::try_parse_encounter_announce(body, blen, timestamp_ticks, source_ipv4, evt)) {
            if (cbs.on_encounter) cbs.on_encounter(cbs.ctx, &evt);
        } else {
            ++malformed_count_;
        }
        return;
    }

    // 02 97 — matchmaking/party-assembly broadcast (multi-member emit)
    if (op0 == 0x02 && op1 == 0x97) {
        if (cbs.on_nickname != nullptr) {
            handlers::try_parse_party_assembly(
                body, blen, timestamp_ticks, source_ipv4,
                cbs.on_nickname, cbs.ctx);
        }

        // Optional dungeon-id extraction from the same packet.
        if (cbs.on_dungeon != nullptr) {
            const int32_t dungeon_id = handlers::extract_dungeon_id(body, blen);
            if (dungeon_id != 0) {
                events::DungeonAnnouncement evt{};
                evt.dungeon_id       = dungeon_id;
                evt.timestamp_ticks  = timestamp_ticks;
                evt.source_ipv4      = source_ipv4;
                cbs.on_dungeon(cbs.ctx, &evt);
            }
        }
        return;
    }

    // 1d 97 — party-leave signal. Empty (4-byte 1d 97 00 00) = "candidate"
    // (host-side room state change like privacy toggle / title edit / reject
    // join), don't fire PartyLeft. Non-empty = real exit, fire.
    // Wire-confirmed bug fix from 사용자 보고 2026-05-03 17:53.
    if (op0 == 0x1d && op1 == 0x97) {
        const bool is_empty_control =
            blen == 4 && body[2] == 0x00 && body[3] == 0x00;
        if (is_empty_control) return;

        events::PartyLeft evt{};
        evt.timestamp_ticks = timestamp_ticks;
        evt.source_ipv4     = source_ipv4;
        if (cbs.on_party_left) cbs.on_party_left(cbs.ctx, &evt);
        return;
    }

    // 0x__ 0x97 / 0x__ 0xe2 family — party-member status enrichment.
    // The handler internally rejects non-party opcodes (02 97, 6a e2,
    // 07 97, 13 97, 2A 97, 04 97). Note that 1d 97 was already caught
    // above so it can't reach here.
    if (op1 == 0x97 || op1 == 0xe2) {
        events::NicknameInfo evt{};
        if (handlers::try_parse_party_member_status(
                body, blen, timestamp_ticks, source_ipv4, evt)) {
            if (cbs.on_nickname) cbs.on_nickname(cbs.ctx, &evt);
        }
        return;
    }

    // No opcode match — silently drop. Many unhandled opcodes in the
    // wire stream (UI updates, chat, etc.) are noise we don't want.
}

}  // namespace aion2fun
