using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x40 0x36 — entity spawn (mobs, NPCs, summons, bosses).
///
/// Two captured wire variants (KR live, 2026-04 빌드):
///
/// Variant A (no name) — emitted for mobs/NPCs without an owner:
///   offset 0-1     opcode 40 36
///   offset 2..     VarInt entityId
///   +1             type byte (0x0c / 0x04 / 0x22 observed)
///   +2             0x__ 0x00       (size_low, mode_flag = 0x00)
///   +4             mob_code (4-byte LE)
///   ... payload
///
/// Variant B (named, owner-attributed) — emitted for summons/pets:
///   offset 0-1     opcode 40 36
///   offset 2..     VarInt entityId
///   +1             type byte (0x5f and others observed)
///   +2             0x00 0x01       (size_low, mode_flag = 0x01 → name follows)
///   +4             name length (1 byte)
///   +5..+5+L       UTF-8 owner name (e.g., "메인딜러입니다")
///   +5+L..+8+L     mob_code (4-byte LE)
///   ... payload
///
/// We discriminate by inspecting the byte at (2 + varintLen + 2): 0x00 = no-name,
/// 0x01 = named. The owner_name in variant B is the player nickname that owns
/// this entity — the aggregator looks it up in NicknameRegistry to wire summon→player.
/// </summary>
public static class SummonSpawnHandler
{
    // Owner-id scan markers, ported verbatim from A2Viewer.Packet.PacketDispatcher
    // (decompiled tools/a2viewer-src/A2Viewer.Packet/PacketDispatcher.cs:80,82). The
    // wire layout for owner-attributed entities (summons, pets, spirits) places the
    // owner_id 2 LE bytes immediately after the [07 02 06] actor header, which itself
    // appears after an 8-byte 0xFF boundary marker. Variant-B name parsing handles
    // the simple summons that embed an owner_name string; the marker scan handles
    // entities whose owner is only encoded as a numeric id (observed: 정령성
    // spirits in 무의요람, mob_code family 29201xx).
    private static readonly byte[] OwnerBoundaryMarker = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
    private static readonly byte[] OwnerActorHeader = { 0x07, 0x02, 0x06 };

    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out SummonSpawnInfo evt)
    {
        evt = null!;
        if (body.Length < 12) return false;
        if (body[0] != 0x40 || body[1] != 0x36) return false;

        if (!FrameAssembler.TryReadVarInt(body[2..], out int entityId, out int varintLen))
            return false;

        int afterVarInt = 2 + varintLen;
        // Layout after VarInt: [type_byte] [size_low] [mode_flag] ...
        if (afterVarInt + 3 > body.Length) return false;
        byte modeFlag = body[afterVarInt + 2];

        int mobCodeOffset;
        string? ownerName = null;

        if (modeFlag == 0x00)
        {
            // Variant A: mob_code at +3
            mobCodeOffset = afterVarInt + 3;
        }
        else if (modeFlag == 0x01)
        {
            // Variant B: name length at +3, name at +4..+3+L, mob_code at +4+L
            int nameLenOffset = afterVarInt + 3;
            if (nameLenOffset >= body.Length) return false;
            int nameLen = body[nameLenOffset];
            // Plausibility gate: nicknames are 1..50 bytes (Korean UTF-8 is
            // 3 bytes/char, so 50 covers 16+ Hangul chars — well over any
            // legal name). Without this gate, a corrupt 0xFF nameLen passes
            // the bounds check on a large packet and we attach 255 bytes of
            // garbage as the owner name — registered as a phantom nickname
            // (audit 2026-05-04: B8 medium).
            if (nameLen < 1 || nameLen > 50) return false;
            int nameOffset = nameLenOffset + 1;
            if (nameOffset + nameLen > body.Length) return false;

            try
            {
                ownerName = System.Text.Encoding.UTF8.GetString(body.Slice(nameOffset, nameLen));
            }
            catch { ownerName = null; }

            // Strict validator (Hangul + ASCII alnum) — rejects misaligned
            // slices that decoded successfully but contain control bytes /
            // punctuation that no real character name would have.
            if (ownerName != null && !SelfNicknameHandler.IsValidNickname(ownerName))
                ownerName = null;

            mobCodeOffset = nameOffset + nameLen;
        }
        else
        {
            return false;
        }

        if (mobCodeOffset + 4 > body.Length) return false;

        int mobCode = body[mobCodeOffset]
                    | (body[mobCodeOffset + 1] << 8)
                    | (body[mobCodeOffset + 2] << 16)
                    | (body[mobCodeOffset + 3] << 24);

        if (mobCode < 1_000_000 || mobCode > 50_000_000) return false;

        int ownerId = ScanOwnerId(body);

        evt = new SummonSpawnInfo(
            SummonId: entityId,
            OwnerId: ownerId,
            MobCode: mobCode,
            TimestampTicks: timestampTicks,
            SourceIpv4: sourceIpv4,
            OwnerName: ownerName);
        return true;
    }

    /// <summary>
    /// Scans the packet body for the owner_id field that follows the
    /// [FF×8] [07 02 06] marker pair. Returns 0 when the markers aren't
    /// present (mobs with no owner) or the candidate fails the >99 sanity
    /// gate. Algorithm ported from A2Viewer.Packet.PacketDispatcher.TryParseSummon.
    /// </summary>
    private static int ScanOwnerId(ReadOnlySpan<byte> body)
    {
        int searchPos = 0;
        while (searchPos < body.Length)
        {
            int boundaryRel = body[searchPos..].IndexOf(OwnerBoundaryMarker);
            if (boundaryRel < 0) return 0;
            int boundaryAbs = searchPos + boundaryRel;
            int afterBoundary = boundaryAbs + OwnerBoundaryMarker.Length;
            if (afterBoundary >= body.Length) return 0;

            int headerRel = body[afterBoundary..].IndexOf(OwnerActorHeader);
            if (headerRel < 0)
            {
                // No actor header within remainder — keep scanning past this
                // boundary in case the packet has multiple FF×8 sentinels.
                searchPos = afterBoundary;
                continue;
            }
            int headerAbs = afterBoundary + headerRel;
            // Need 5 bytes from header start: [07][02][06][low][high]
            if (headerAbs + 5 > body.Length) return 0;

            int candidate = body[headerAbs + 3] | (body[headerAbs + 4] << 8);
            // >99 gate: A2Viewer rejects ids 0..99 because real player entityIds
            // are 4-6 digits. Without this, random low-byte coincidences inside
            // mob payloads would leak as phantom owner ids.
            if (candidate > 99) return candidate;

            searchPos = boundaryAbs + 1;
        }
        return 0;
    }
}
