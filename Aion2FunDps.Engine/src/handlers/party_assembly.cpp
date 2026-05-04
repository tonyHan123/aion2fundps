// party_assembly.cpp — implementation. Direct port of
// PartyAssemblyHandler.cs preserving all wire-format quirks documented
// there (CP layout probing, server-id offset detection, entity-id
// pre-name-len position, etc.).

#include "party_assembly.h"
#include "../varint.h"

#include <unordered_set>

namespace aion2fun::handlers {

namespace {

inline int32_t read_i32_le(const uint8_t* body, size_t offset, size_t length) noexcept {
    if (offset + 4 > length) return 0;
    return static_cast<int32_t>(
        static_cast<uint32_t>(body[offset])
        | (static_cast<uint32_t>(body[offset + 1]) << 8)
        | (static_cast<uint32_t>(body[offset + 2]) << 16)
        | (static_cast<uint32_t>(body[offset + 3]) << 24));
}

inline bool is_known_server_id(int32_t id) noexcept {
    return (id >= 1001 && id <= 1021) || (id >= 2001 && id <= 2021);
}

// Plausible nickname check: validate Korean continuation bytes inline.
// Required because op=0297 / op=0197 records embed nicknames within
// arbitrary-byte trailers; without bytewise validation, garbage like
// `ec 03 ec` would decode to U+FFFD '�' and surface as a phantom party row.
// Unlike the strict ASCII+Hangul validator used elsewhere, this one is
// the byte-level pre-check that runs BEFORE we'd even commit to a
// particular nickname — must accept the same character classes
// (Hangul + ASCII alnum) but works directly on bytes for speed.
bool looks_like_nickname(const uint8_t* body, size_t offset, size_t length, size_t buf_end) noexcept {
    int printable = 0;
    size_t end = offset + length;
    if (end > buf_end) return false;
    size_t i = offset;
    while (i < end) {
        const uint8_t b = body[i];
        if (b >= 0xEA && b <= 0xED) {
            // 3-byte UTF-8 lead for Hangul. Validate continuation bytes
            // 80..BF range.
            if (i + 2 >= end) return false;
            const uint8_t b1 = body[i + 1];
            const uint8_t b2 = body[i + 2];
            if (b1 < 0x80 || b1 > 0xBF) return false;
            if (b2 < 0x80 || b2 > 0xBF) return false;
            ++printable;
            i += 3;
            continue;
        }
        if ((b >= '0' && b <= '9') ||
            (b >= 'A' && b <= 'Z') ||
            (b >= 'a' && b <= 'z')) {
            ++printable;
            ++i;
            continue;
        }
        return false;
    }
    return printable >= 1;
}

bool is_plausible_entity_id_at(const uint8_t* body, size_t offset, size_t limit) noexcept {
    if (offset + 8 >= limit) return false;
    const int32_t cand = read_i32_le(body, offset, limit);
    return cand > 0 && cand < 100'000'000
        && body[offset + 3] == 0x00
        && body[offset + 4] == 0x00
        && body[offset + 5] == 0x00
        && body[offset + 8] >= 3
        && body[offset + 8] <= 33;
}

}  // anonymous namespace

void scan_for_nicknames(
    const uint8_t* body, size_t body_length,
    size_t start_pos, size_t end_pos,
    int32_t group_id_hint,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    PartyMemberCallback member_cb,
    void* ctx) noexcept
{
    if (body == nullptr || member_cb == nullptr) return;
    if (end_pos > body_length) end_pos = body_length;
    if (end_pos < start_pos + 16) return;

    size_t pos = start_pos;
    int emitted = 0;
    constexpr int kMaxEmit = 24;

    // Dedupe — byte-by-byte advance can re-detect the same nickname at
    // different alignments. Track seen entity_ids.
    std::unordered_set<int32_t> seen;

    while (pos + 4 < end_pos && emitted < kMaxEmit) {
        const int32_t name_len = body[pos];
        if (name_len < 3 || name_len > 33) {
            ++pos;
            continue;
        }
        const size_t name_start = pos + 1;
        if (name_start + static_cast<size_t>(name_len) + 8 > end_pos) break;

        if (!looks_like_nickname(body, name_start, static_cast<size_t>(name_len), end_pos)) {
            ++pos;
            continue;
        }

        // entity_id 9 bytes BEFORE name_len. Layout: [marker 2-3][entityId 6][serverId 2][nameLen 1][name N]
        if (name_start < 9) { ++pos; continue; }
        const size_t entity_id_offset = name_start - 9;
        if (entity_id_offset + 4 > end_pos) { ++pos; continue; }
        const int32_t entity_id = read_i32_le(body, entity_id_offset, end_pos);

        // serverId in 2 bytes immediately before nameLen.
        int32_t server_id = 0;
        if (name_start >= 3) {
            const int32_t cand = static_cast<int32_t>(
                static_cast<uint32_t>(body[name_start - 3])
                | (static_cast<uint32_t>(body[name_start - 2]) << 8));
            if (is_known_server_id(cand)) server_id = cand;
        }

        // CP probe — Short / Long A / Long B/C layouts via server-id anchor
        // detection. See PartyAssemblyHandler.cs for the wire-confirmed
        // commentary; we mirror the priority order here exactly.
        const size_t stats_offset = name_start + static_cast<size_t>(name_len);
        int cp_offset = -1;
        if (stats_offset + 9 <= end_pos) {
            const int32_t cand = static_cast<int32_t>(
                static_cast<uint32_t>(body[stats_offset + 8])
                | (static_cast<uint32_t>(body[stats_offset + 9]) << 8));
            if (is_known_server_id(cand)) cp_offset = static_cast<int>(stats_offset + 13);  // Short
        }
        if (cp_offset < 0 && stats_offset + 13 <= end_pos) {
            const int32_t cand = static_cast<int32_t>(
                static_cast<uint32_t>(body[stats_offset + 12])
                | (static_cast<uint32_t>(body[stats_offset + 13]) << 8));
            if (is_known_server_id(cand)) cp_offset = static_cast<int>(stats_offset + 17);  // Long A
        }
        if (cp_offset < 0 && stats_offset + 14 <= end_pos) {
            const int32_t cand = static_cast<int32_t>(
                static_cast<uint32_t>(body[stats_offset + 13])
                | (static_cast<uint32_t>(body[stats_offset + 14]) << 8));
            if (is_known_server_id(cand)) cp_offset = static_cast<int>(stats_offset + 18);  // Long B/C
        }

        int32_t combat_power = 0;
        if (cp_offset >= 0 && static_cast<size_t>(cp_offset) + 4 <= end_pos) {
            const int32_t cp_raw = read_i32_le(body, static_cast<size_t>(cp_offset), end_pos);
            if (cp_raw >= 10'000 && cp_raw <= 999'999) combat_power = cp_raw;
        }

        // jobCode at statsOffset+0 (4 LE), 1..40 plausible.
        int32_t job = 0;
        if (stats_offset + 4 <= end_pos) {
            const int32_t job_raw = read_i32_le(body, stats_offset, end_pos);
            if (job_raw >= 1 && job_raw <= 40) job = job_raw;
        }

        if (is_plausible_entity_id_at(body, entity_id_offset, end_pos)
            && seen.insert(entity_id).second)
        {
            events::NicknameInfo evt{};
            evt.user_id          = entity_id;
            evt.nickname         = reinterpret_cast<const char*>(body + name_start);
            evt.nickname_len     = name_len;
            evt.is_self          = 0;
            evt.server           = server_id;
            evt.job              = job;
            evt.combat_power     = combat_power;
            evt.timestamp_ticks  = timestamp_ticks;
            evt.source_ipv4      = source_ipv4;
            evt.is_party_member  = 1;
            evt.is_roster_start  = (emitted == 0) ? 1 : 0;
            evt.room_id          = group_id_hint;
            member_cb(ctx, &evt);
            ++emitted;
        }

        // Conservative advance: past name + 4-byte size flag. Records carry
        // varying trailer sizes; a fixed +16 advance overshoots and skips
        // the next nickname. Dedupe set handles spurious re-matches.
        pos = name_start + static_cast<size_t>(name_len) + 4;
    }
}

int32_t try_parse_party_assembly(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    PartyMemberCallback member_cb,
    void* ctx) noexcept
{
    if (body == nullptr || body_length < 16) return 0;
    if (body[0] != 0x02 || body[1] != 0x97) return 0;

    // Sender VarInt at offset 2 — used as room/group identifier.
    const int32_t group_id = read_i32_le(body, 2, body_length);

    if (member_cb != nullptr) {
        scan_for_nicknames(body, body_length,
                           /*start_pos=*/8, /*end_pos=*/body_length,
                           group_id, timestamp_ticks, source_ipv4,
                           member_cb, ctx);
    }
    return group_id;
}

namespace {

// Trampoline that lets scan_for_nicknames feed a callback whose ctx is
// a pointer to a std::vector<events::NicknameInfo>. The lambda copies
// each emitted event into the vector. Used by both the op=0297 collect
// path and the multi-room scan in op=0197.
void vec_collect_cb(void* ctx, const events::NicknameInfo* evt) noexcept {
    auto* vec = static_cast<std::vector<events::NicknameInfo>*>(ctx);
    vec->push_back(*evt);
}

// Room header detection mirrors PartyAssemblyHandler.TryReadRoomHeader.
// Layout: [groupId 4][nameLen 1][name N][04 ?? ?? ?? ?? 00 03]
// Returns true if header_end is set to nameStart+nameLength.
bool try_read_room_header(
    const uint8_t* body, size_t body_length,
    size_t offset,
    int32_t& out_group_id,
    size_t& out_header_end) noexcept
{
    if (offset + 6 > body_length) return false;

    const int32_t group_id = read_i32_le(body, offset, body_length);
    if (group_id <= 0 || group_id > 100'000'000) return false;

    const int name_length = body[offset + 4];
    if (name_length <= 0 || name_length > 80) return false;

    const size_t name_start = offset + 5;
    if (name_start + static_cast<size_t>(name_length) > body_length) return false;
    const size_t name_end = name_start + static_cast<size_t>(name_length);

    // The 04 ?? ?? ?? ?? 00 03 trailer is the structural anchor that
    // separates a real room header from a member-record byte sequence
    // that happens to look like [id][len][utf8].
    if (name_end + 7 > body_length) return false;
    if (body[name_end] != 0x04) return false;
    if (body[name_end + 5] != 0x00) return false;
    if (body[name_end + 6] != 0x03) return false;

    // Quick UTF-8 plausibility — accept Hangul (3-byte EA-ED leads with
    // 80-BF continuations) and ASCII printable. Avoids pulling in the
    // strict StrictUtf8 validator since we don't need the room-name
    // string itself, only the boundary detection.
    int useful = 0;
    for (size_t i = name_start; i < name_end; ) {
        const uint8_t b = body[i];
        if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D) return false;  // control
        if (b >= 0xEA && b <= 0xED) {
            if (i + 2 >= name_end) return false;
            const uint8_t b1 = body[i + 1];
            const uint8_t b2 = body[i + 2];
            if (b1 < 0x80 || b1 > 0xBF) return false;
            if (b2 < 0x80 || b2 > 0xBF) return false;
            ++useful;
            i += 3;
            continue;
        }
        if (b >= 0x80 && b < 0xEA) return false;  // unexpected non-ASCII
        if (b > 0xED) return false;
        if (b > 0x20) ++useful;
        ++i;
    }
    if (useful == 0) return false;

    out_group_id   = group_id;
    out_header_end = name_end;
    return true;
}

}  // anonymous namespace

int32_t parse_party_assembly_collect(
    const uint8_t* body, size_t body_length,
    int64_t timestamp_ticks,
    uint32_t source_ipv4,
    std::vector<events::NicknameInfo>& out_members) noexcept
{
    out_members.clear();
    if (body == nullptr || body_length < 16) return 0;
    if (body[0] != 0x02 || body[1] != 0x97) return 0;

    const int32_t group_id = read_i32_le(body, 2, body_length);
    scan_for_nicknames(body, body_length,
                       /*start_pos=*/8, /*end_pos=*/body_length,
                       group_id, timestamp_ticks, source_ipv4,
                       &vec_collect_cb, &out_members);
    return group_id;
}

std::vector<RoomBlock> parse_room_blocks(
    const uint8_t* body, size_t body_length,
    size_t start_pos,
    int64_t timestamp_ticks,
    uint32_t source_ipv4) noexcept
{
    std::vector<RoomBlock> result;
    if (body == nullptr || body_length < start_pos + 16) return result;

    // First pass: locate every plausible room header. Headers are
    // sparse vs body size, so a flat vector is fine.
    struct Hdr { int32_t group_id; size_t offset; size_t header_end; };
    std::vector<Hdr> headers;
    size_t pos = start_pos;
    while (pos + 6 <= body_length) {
        int32_t gid = 0;
        size_t hend = 0;
        if (try_read_room_header(body, body_length, pos, gid, hend)) {
            headers.push_back({ gid, pos, hend });
            // Skip past the header so we don't re-detect inside the name.
            pos = hend;
            continue;
        }
        ++pos;
    }

    // Second pass: scan each room's body slice (from header_end to next
    // header's offset, or end-of-body for the last header).
    for (size_t i = 0; i < headers.size(); ++i) {
        const auto& h = headers[i];
        const size_t scan_start = h.header_end;
        const size_t scan_end = (i + 1 < headers.size())
            ? headers[i + 1].offset
            : body_length;
        if (scan_start >= scan_end) continue;

        RoomBlock blk;
        blk.group_id = h.group_id;
        scan_for_nicknames(body, body_length,
                           scan_start, scan_end,
                           h.group_id,
                           timestamp_ticks, source_ipv4,
                           &vec_collect_cb, &blk.members);
        result.push_back(std::move(blk));
    }

    return result;
}

int32_t extract_dungeon_id(const uint8_t* body, size_t body_length) noexcept
{
    // Layout: [02 97][groupId 4][nameLen varint][name][count 1][dungeonId 4][stage 1]
    if (body == nullptr || body_length < 11) return 0;
    if (body[0] != 0x02 || body[1] != 0x97) return 0;
    // High byte of groupId is 0 for matchmaking ids in this range.
    if (body[5] != 0) return 0;

    size_t pos = 6;
    const auto vi = try_read_varint(body + pos, body_length - pos);
    if (!vi.ok) return 0;
    const int32_t name_len = vi.value;
    if (name_len < 1 || name_len > 200) return 0;
    pos += vi.bytes_read + static_cast<size_t>(name_len);
    if (pos + 5 > body_length) return 0;

    const uint8_t count_byte = body[pos];
    if (count_byte != 4 && count_byte != 8) return 0;
    ++pos;

    const int32_t dungeon_id = read_i32_le(body, pos, body_length);
    return (dungeon_id >= 600'000 && dungeon_id < 700'000) ? dungeon_id : 0;
}

}  // namespace aion2fun::handlers
