using System;
using System.Collections.Generic;
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x6a 0xe2 — bulk party/zone broadcast packet.
///
/// Issued by the server at zone-load (dungeon entry, party assembly room) and at
/// other transitions; carries multiple party-member records in a single payload
/// (~200 byte for 3 members, ~1.7K for 8 members + pets). Capturing this gives
/// us entity_id↔nickname mappings *immediately* on dungeon entry — without it,
/// we have to wait for OTHER_NICK to trickle in from the visibility cone.
///
/// Captured layout (KR live, 2026-04 빌드):
///   header (12 bytes):
///     0-1   opcode 6a e2
///     2-3   record count (LE u16)
///     4-11  zone + flags + padding
///   each record (variable, name-length-dependent, ~30~50 bytes):
///     +0    zone (2 bytes)
///     +2    name_len (1 byte)
///     +3..  UTF-8 nickname (name_len bytes)
///     ...   level (4 bytes), 8-byte account/persistent id, entity_id (4 bytes), padding
///
/// The exact record boundaries vary across observed packets — some records carry
/// extra trailer bytes (entity_id + zone marker + padding) and some don't. Rather
/// than getting boundaries exactly right, we byte-scan for valid length-prefixed
/// UTF-8 nicknames and emit one NicknameInfo per match. The entity_id we attach
/// is heuristic — extracted from a candidate offset window after the name. Wrong
/// entity_id won't crash anything; the mob_code guard in IsPlayerDamage will
/// stop a misclassification from rejecting boss damage.
/// </summary>
public static class PartyBulkInfoHandler
{
    public static List<NicknameInfo> ParseAll(
        byte[] body,
        long timestampTicks,
        uint sourceIpv4)
    {
        var result = new List<NicknameInfo>();
        // Disabled 2026-05-01: A2Viewer reverse engineering confirmed op=0x6a
        // 0xe2 is NOT a party-member broadcast. A2Viewer's PartyStreamParser
        // contains zero references to this opcode — its full party pipeline
        // runs on the 0x__ 0x97 family (01 / 02 / 07 / 0B / 1D / etc.) plus
        // the standalone 0x00 0x92 CP broadcast.
        //
        // The packets we'd been treating as "zone-load roster" were actually
        // the friend list / online population broadcast (one capture parsed
        // 32 names — 루갈, 휘봉, 자라비, 우민지, 컬리, 이양갱 etc. — which
        // the user reported as "친구목록 사람들이 미터기에 쭉 뜨네"). The
        // dispatcher still routes the opcode here so the bulk-debug log keeps
        // capturing it for diagnostics, but we no longer surface it as a
        // party broadcast. Real party tracking flows through op=0297 (lobby)
        // and op=0092 (mid-dungeon CP refresh).
        return result;

        // ---- legacy parser kept below, unreachable ----
        if (body.Length < 16) return result;
        if (body[0] != 0x6a || body[1] != 0xe2) return result;

        int declaredRecords = body[2] | (body[3] << 8);
        if (declaredRecords > 16) return result;

        int pos = 12;
        int emitted = 0;
        const int maxEmit = 16;

        while (pos + 4 < body.Length && emitted < maxEmit)
        {
            int nameLen = body[pos + 2];

            // name_len plausibility: Korean UTF-8 is 3 bytes/char; 3~33 bytes
            // covers 1~11 chars. Reject otherwise — padding, not a record header.
            if (nameLen < 3 || nameLen > 33)
            {
                pos++;
                continue;
            }

            int nameStart = pos + 3;
            if (nameStart + nameLen + 16 > body.Length) break;

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

            int entityId = ScanEntityId(body, nameStart + nameLen, scanWindow: 24);
            if (entityId > 0)
            {
                result.Add(new NicknameInfo(
                    UserId: entityId,
                    Nickname: nickname,
                    IsSelf: false,
                    Server: 0,
                    Job: 0,
                    CombatPower: 0,
                    TimestampTicks: timestampTicks,
                    SourceIpv4: sourceIpv4,
                    IsPartyMember: true,
                    IsRosterStart: emitted == 0));
                emitted++;
            }

            // Advance past this record. Step `nameLen + 22` past the name
            // skips the typical trailer (sep + flag + level + filler + flag + ID).
            // If we miss a boundary, next iteration's plausibility check re-syncs.
            pos = nameStart + nameLen + 22;
        }
        return result;
    }

    private static bool LooksLikeNickname(byte[] body, int offset, int length)
    {
        int printable = 0;
        int end = offset + length;
        for (int i = offset; i < end; i++)
        {
            byte b = body[i];
            if (b >= 0xEA && b <= 0xED) { printable++; i += 2; continue; }  // Hangul prefix
            if ((b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z'))
            {
                printable++;
            }
            else if (b < 0x20 || b == 0x7F)
            {
                return false;
            }
        }
        return printable > 0;
    }

    private static int ScanEntityId(byte[] body, int startOffset, int scanWindow)
    {
        int end = Math.Min(body.Length - 4, startOffset + scanWindow);
        for (int i = startOffset; i <= end; i++)
        {
            int candidate = body[i] | (body[i + 1] << 8) | (body[i + 2] << 16) | (body[i + 3] << 24);
            // Plausible entity_id range: > 0, < 100M, high byte 0x00.
            if (candidate > 0 && candidate < 100_000_000 && body[i + 3] == 0x00)
            {
                return candidate;
            }
        }
        return 0;
    }
}
