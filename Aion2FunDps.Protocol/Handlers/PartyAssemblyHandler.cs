using System;
using System.Collections.Generic;
using System.Text;
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x02 0x97 — matchmaking/party-assembly broadcast.
///
/// Issued by NCSoft's lobby server (a different IP block than the game zone
/// server, hence why the original /24 BPF filter missed these). Carries the
/// matchmaking room name plus per-member records (nickname + entity_id +
/// level + 8-byte account ID + zone). Discovering this opcode is what makes
/// "party visible in matchmaking room" work — without it the leaderboard
/// stays blank until the user lands their first hit on a boss.
///
/// Captured layout (KR live, 2026-04 빌드):
///   0-1     opcode 02 97
///   2-5     room_id uint32 LE
///   6       room_name_len (1 byte)
///   7..     room_name UTF-8
///   ...     room metadata
///   per-member record (variable):
///     ...   8-byte account ID
///     ...   misc bytes
///     ...   8-byte account ID (repeated)
///     +0    name_len (1 byte)
///     +1..  UTF-8 nickname (name_len bytes)
///     +N    level (4-byte LE)
///     +N+4  zone / additional metadata
///     ...   entity_id (4-byte LE) somewhere in the next 16 bytes
///
/// Record boundaries are imprecise. Rather than trying to parse the layout
/// exactly, we byte-scan for valid length-prefixed UTF-8 nicknames and emit
/// one NicknameInfo per match — the entity_id we attach is heuristic, picked
/// from a candidate window after the name. Wrong entity_id can't crash
/// anything; the mob_code guard in IsPlayerDamage stops misclassifications
/// from rejecting boss damage.
/// </summary>
public static class PartyAssemblyHandler
{
    public readonly record struct ParseResult(int GroupId, List<NicknameInfo> Members);
    public readonly record struct RoomBlock(int GroupId, int Offset, int EndOffset, string RoomName, List<NicknameInfo> Members);

    private readonly record struct RoomHeader(int GroupId, int Offset, int NameLength, string RoomName);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static ParseResult ParseAll(
        byte[] body,
        long timestampTicks,
        uint sourceIpv4)
    {
        if (body.Length < 16) return new ParseResult(0, new List<NicknameInfo>());
        if (body[0] != 0x02 || body[1] != 0x97) return new ParseResult(0, new List<NicknameInfo>());

        // Sender VarInt at offset 2 — used as room/group identifier so the
        // aggregator can detect when a different matchmaking room sends the
        // broadcast (vs. the same room rebroadcasting at ~3Hz). Returned
        // separately from the member list so an empty roster (parsed=0)
        // still carries the room id — needed by the aggregator to decide
        // "different room → wipe" vs. "same room, parse blip → keep".
        int groupId = ReadLittleEndianInt32(body, 2);

        var members = ScanForNicknames(body, startPos: 8, groupId, timestampTicks, sourceIpv4);
        return new ParseResult(groupId, members);
    }

    public static List<RoomBlock> ParseRoomBlocks(
        byte[] body,
        int startPos,
        long timestampTicks,
        uint sourceIpv4)
    {
        var result = new List<RoomBlock>();
        if (body.Length < startPos + 16) return result;

        var headers = new List<RoomHeader>();
        int pos = Math.Max(0, startPos);
        while (pos + 6 <= body.Length)
        {
            if (TryReadRoomHeader(body, pos, out var header))
            {
                headers.Add(header);
                pos += 5 + header.NameLength;
                continue;
            }

            pos++;
        }

        for (int i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            int scanStart = header.Offset + 5 + header.NameLength;
            int end = i + 1 < headers.Count ? headers[i + 1].Offset : body.Length;
            if (scanStart >= end) continue;

            var members = ScanForNicknames(
                body,
                startPos: scanStart,
                endPos: end,
                groupId: header.GroupId,
                timestampTicks,
                sourceIpv4);

            result.Add(new RoomBlock(header.GroupId, header.Offset, end, header.RoomName, members));
        }

        return result;
    }

    /// <summary>
    /// Public byte-scanner that finds the same per-member roster format used
    /// across multiple opcodes — op=0297 (matchmaking broadcast), op=0197
    /// (encounter announce, includes the full party roster). Caller supplies
    /// the start position (past whatever opcode-specific header precedes the
    /// roster) and a groupId hint (zero is fine when there's no matchmaking
    /// room context). Without this generic scan we'd miss party members
    /// announced via op=0197 at zone-load.
    /// </summary>
    public static List<NicknameInfo> ScanForNicknames(
        byte[] body,
        int startPos,
        int groupId,
        long timestampTicks,
        uint sourceIpv4)
        => ScanForNicknames(body, startPos, body.Length, groupId, timestampTicks, sourceIpv4);

    public static List<NicknameInfo> ScanForNicknames(
        byte[] body,
        int startPos,
        int endPos,
        int groupId,
        long timestampTicks,
        uint sourceIpv4)
    {
        var result = new List<NicknameInfo>();
        int limit = Math.Clamp(endPos, 0, body.Length);
        if (limit < startPos + 16) return result;

        int pos = startPos;
        int emitted = 0;
        const int maxEmit = 24;  // 8-man + assembly slot churn
        // Dedupe: an aggressive byte-by-byte advance can re-detect the same
        // nickname twice in different alignments. Track seen entity_ids.
        var seen = new HashSet<int>();

        while (pos + 4 < limit && emitted < maxEmit)
        {
            int nameLen = body[pos];

            // Plausible nickname length 3~33 bytes (1~11 Korean chars or mixed).
            if (nameLen < 3 || nameLen > 33)
            {
                pos++;
                continue;
            }

            int nameStart = pos + 1;
            if (nameStart + nameLen + 8 > limit) break;

            if (!LooksLikeNickname(body, nameStart, nameLen))
            {
                pos++;
                continue;
            }

            string nickname;
            try
            {
                nickname = System.Text.Encoding.UTF8.GetString(body, nameStart, nameLen);
            }
            catch
            {
                pos++;
                continue;
            }

            // entity_id lives 9 bytes BEFORE the name_len byte (record header
            // layout: [marker 2-3 bytes][entityId 6 bytes (4 used)][job/class 2][nameLen 1][name N]).
            // Earlier code read +8 PAST the name (CP field), which is 0 in fresh
            // rooms before stats sync — that produced parsed=0 broadcasts even
            // when 짭호 was clearly visible in the packet. Reading entityId from
            // BEFORE the name aligns with the actual NCSoft layout.
            int entityIdOffset = nameStart - 9;
            if (entityIdOffset < 0 || entityIdOffset + 4 > limit)
            {
                pos++;
                continue;
            }
            int entityId = ReadLittleEndianInt32(body, entityIdOffset);
            // ServerId lives in the 2 bytes immediately before nameLen, LE
            // uint16. Layout: [entityId 6][serverId 2][nameLen 1][name N].
            // This is what produces "닉[바바]" suffixes in the in-game
            // lobby for cross-server members; without it every member shows
            // as same-server and we don't know to render the suffix.
            int serverId = 0;
            if (nameStart - 3 >= 0 && nameStart - 2 >= 0)
            {
                int candidate = body[nameStart - 3] | (body[nameStart - 2] << 8);
                if (Aion2FunDps.Core.Models.ServerMap.IsKnownServerId(candidate))
                    serverId = candidate;
            }
            // Stats trail after the name has THREE observed layouts. The CP
            // 4-byte field is ALWAYS preceded by a 0x04 marker, which itself
            // is preceded by a 2-byte server-id (LE u16). The server-id is
            // the reliable anchor — different layouts insert filler/flag
            // bytes earlier in the trailer, but the [server][server][04][CP]
            // tail is invariant. Wire-confirmed across 5 op=0297 captures of
            // 짭호+앞집누나+크라락+이양갱 from 무의 요람 entry, May 2026.
            //
            //   Short (host-only Strong):
            //     [job 4][? 4][server 2][server 2][04][CP 4]
            //     → first server at statsOffset+8, CP at +13
            //
            //   Long A (regular multi-member):
            //     [job 4][? 4][unk 4][server 2][server 2][04][CP 4]
            //     → first server at +12, CP at +17
            //
            //   Long B/C (member-specific flag inserted mid-trailer):
            //     [job 4][? 4][unk 4][flag 1][server 2][???? 2][04][CP 4]
            //     → first server at +13, CP at +18
            //
            // The flag byte (0x00 or 0x01) appears for some members on some
            // packets — it's NOT consistent per-room or per-character; the
            // same player can carry it in one broadcast and not the next.
            // Earlier code only checked +8 vs +12, so Long B/C records were
            // mis-classified as Long A and CP was read from +17 instead of
            // +18 — landing on `[04][CP first 3 bytes]` and producing values
            // like 82.8M. The fix: probe the server-id position (+8/+12/+13)
            // in priority order; whichever hits a known server determines
            // the CP offset.
            int statsOffset = nameStart + nameLen;
            int cpOffset = -1;
            if (statsOffset + 9 <= limit
                && Aion2FunDps.Core.Models.ServerMap.IsKnownServerId(
                    body[statsOffset + 8] | (body[statsOffset + 9] << 8)))
            {
                cpOffset = statsOffset + 13;  // Short layout
            }
            else if (statsOffset + 13 <= limit
                && Aion2FunDps.Core.Models.ServerMap.IsKnownServerId(
                    body[statsOffset + 12] | (body[statsOffset + 13] << 8)))
            {
                cpOffset = statsOffset + 17;  // Long A
            }
            else if (statsOffset + 14 <= limit
                && Aion2FunDps.Core.Models.ServerMap.IsKnownServerId(
                    body[statsOffset + 13] | (body[statsOffset + 14] << 8)))
            {
                cpOffset = statsOffset + 18;  // Long B/C (flagged)
            }
            int combatPower = 0;
            if (cpOffset >= 0 && cpOffset + 4 <= limit)
            {
                int cpRaw = ReadLittleEndianInt32(body, cpOffset);
                // Backstop sanity: real Aion 2 KR CPs top out around 1M, so
                // anything past 10M is a parser miss the layout-detection
                // didn't catch. Drop instead of displaying garbage.
                if (cpRaw >= 10_000 && cpRaw <= 999_999) combatPower = cpRaw;
            }

            // jobCode at statsOffset+0 (4-byte LE). Common across Long A/B/C
            // — only the trailer (server/CP) shifts on flag-byte presence,
            // the header (job/level) is invariant. Verified against captured
            // op=0297 records (짭호=6, 앞집누나=14, 크라락=14, 이양갱) and
            // A2Viewer's TryReadFixedStats (PartyStreamParser.cs:1043).
            // Plausibility 1..40 matches the in-game class roster.
            int job = 0;
            if (statsOffset + 4 <= limit)
            {
                int jobRaw = ReadLittleEndianInt32(body, statsOffset);
                if (jobRaw >= 1 && jobRaw <= 40) job = jobRaw;
            }
            if (IsPlausibleEntityIdAt(body, entityIdOffset, limit) && seen.Add(entityId))
            {
                result.Add(new NicknameInfo(
                    UserId: entityId,
                    Nickname: nickname,
                    IsSelf: false,
                    Server: serverId,
                    Job: job,
                    CombatPower: combatPower,
                    TimestampTicks: timestampTicks,
                    SourceIpv4: sourceIpv4,
                    IsPartyMember: true,
                    IsRosterStart: emitted == 0,
                    GroupId: groupId));
                emitted++;
            }

            // Conservative advance: only past the nickname itself + 4 bytes
            // (size flag), then re-scan from there. Records carry varying
            // amounts of extra metadata (extra 8-byte account-id blocks for
            // some members) — a fixed +16 advance overshoots and skips the
            // next nickname. The dedupe set handles spurious re-matches.
            pos = nameStart + nameLen + 4;
        }
        return result;
    }

    public static int ReadLittleEndianInt32(byte[] body, int offset)
    {
        if (offset < 0 || offset + 4 > body.Length) return 0;
        return body[offset]
             | (body[offset + 1] << 8)
             | (body[offset + 2] << 16)
             | (body[offset + 3] << 24);
    }

    /// <summary>
    /// Scan op=0297 body for the dungeon id that sits right after the
    /// room-name + 1-byte count byte (=4 or 8). KR live values land in
    /// 600000-699999. Returns 0 when the packet doesn't carry a dungeon
    /// signal (e.g. lobby browse without a selection). Mirrors A2Viewer's
    /// ScanDungeonIdRaw.
    /// </summary>
    public static int ExtractDungeonId(byte[] body)
    {
        // Layout: [02 97][groupId 4][nameLen varint][name][count 1][dungeonId 4][stage 1]
        if (body.Length < 11) return 0;
        if (body[0] != 0x02 || body[1] != 0x97) return 0;
        // groupId byte 4 (high) is 0 for matchmaking ids in this range.
        if (body[5] != 0) return 0;

        int pos = 6;
        if (!FrameAssembler.TryReadVarInt(body.AsSpan(pos), out int nameLen, out int varBytes))
            return 0;
        if (nameLen < 1 || nameLen > 200) return 0;
        pos += varBytes + nameLen;
        if (pos + 5 > body.Length) return 0;

        int countByte = body[pos];
        if (countByte != 4 && countByte != 8) return 0;
        pos++;

        int dungeonId = ReadLittleEndianInt32(body, pos);
        return (dungeonId >= 600_000 && dungeonId < 700_000) ? dungeonId : 0;
    }

    private static bool TryReadRoomHeader(byte[] body, int offset, out RoomHeader header)
    {
        header = default;
        if (offset < 0 || offset + 6 > body.Length) return false;

        int groupId = ReadLittleEndianInt32(body, offset);
        if (groupId <= 0 || groupId > 100_000_000) return false;

        int nameLength = body[offset + 4];
        if (nameLength <= 0 || nameLength > 80) return false;

        int nameStart = offset + 5;
        if (nameStart + nameLength > body.Length) return false;
        int nameEnd = nameStart + nameLength;

        // Room headers are immediately followed by the stable room metadata
        // prefix `04 <server-ish uint32> 00 03`. Without this guard, member
        // records inside a room can look like a plausible `[id][len][utf8]`
        // pair and split the current room before its roster is scanned.
        if (nameEnd + 7 > body.Length) return false;
        if (body[nameEnd] != 0x04) return false;
        if (body[nameEnd + 5] != 0x00) return false;
        if (body[nameEnd + 6] != 0x03) return false;

        string roomName;
        try
        {
            roomName = StrictUtf8.GetString(body, nameStart, nameLength);
        }
        catch
        {
            return false;
        }

        if (!LooksLikeRoomName(roomName)) return false;
        header = new RoomHeader(groupId, offset, nameLength, roomName);
        return true;
    }

    private static bool LooksLikeRoomName(string roomName)
    {
        if (string.IsNullOrWhiteSpace(roomName)) return false;
        if (roomName.Length > 40) return false;

        int useful = 0;
        foreach (char c in roomName)
        {
            if (char.IsControl(c)) return false;
            if (!char.IsWhiteSpace(c)) useful++;
        }

        return useful > 0;
    }

    private static bool LooksLikeNickname(byte[] body, int offset, int length)
    {
        // Validate Korean continuation bytes — without this, inter-record meta
        // like `ec 03 ec` decodes to U+FFFD '�' and emits a phantom party row.
        // Require ≥2 chars: every legit Aion nick is multi-syllable, single-char
        // matches in the byte stream are almost always false positives.
        int printable = 0;
        int end = offset + length;
        int i = offset;
        while (i < end)
        {
            byte b = body[i];
            if (b >= 0xEA && b <= 0xED)
            {
                if (i + 2 >= end) return false;
                byte b1 = body[i + 1];
                byte b2 = body[i + 2];
                if (b1 < 0x80 || b1 > 0xBF) return false;
                if (b2 < 0x80 || b2 > 0xBF) return false;
                printable++;
                i += 3;
                continue;
            }
            if ((b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z'))
            {
                printable++;
                i++;
                continue;
            }
            return false;
        }
        return printable >= 1;
    }

    private static bool IsPlausibleEntityIdAt(byte[] body, int offset)
        => IsPlausibleEntityIdAt(body, offset, body.Length);

    private static bool IsPlausibleEntityIdAt(byte[] body, int offset, int limit)
    {
        if (offset < 0 || offset + 8 >= limit) return false;

        int candidate = ReadLittleEndianInt32(body, offset);

        // Do not require candidate >= 1000. Lobby entity ids can be small
        // (observed 813), and rejecting them makes joiners invisible until a
        // later status/nickname packet arrives. The surrounding record shape is
        // the stronger guard: entity id, two zero bytes, zone, then name_len.
        return candidate > 0
            && candidate < 100_000_000
            && body[offset + 3] == 0x00
            && body[offset + 4] == 0x00
            && body[offset + 5] == 0x00
            && body[offset + 8] >= 3
            && body[offset + 8] <= 33;
    }

    private static int ScanEntityId(byte[] body, int startOffset, int scanWindow)
    {
        int end = Math.Min(body.Length - 4, startOffset + scanWindow);
        for (int i = startOffset; i <= end; i++)
        {
            int candidate = body[i] | (body[i + 1] << 8) | (body[i + 2] << 16) | (body[i + 3] << 24);
            if (candidate > 0 && candidate < 100_000_000 && body[i + 3] == 0x00)
            {
                return candidate;
            }
        }
        return 0;
    }
}
