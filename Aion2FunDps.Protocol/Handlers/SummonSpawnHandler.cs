using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x40 0x36 — summon entity spawn. Maps the live summon entity ID
/// to the player who owns it, and to the mob template code.
///
/// Layout (TK parseSummonPacket): byte-pattern search heavy — multiple anchor sequences
/// within the body to locate (mobCode) and (ownerId). See TK source for details.
/// </summary>
public static class SummonSpawnHandler
{
    private static readonly byte[] MobCodeMarkerA = { 0x00, 0x40, 0x02 };
    private static readonly byte[] MobCodeMarkerB = { 0x00, 0x00, 0x02 };
    private static readonly byte[] OwnerKeyMarker = { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
    private static readonly byte[] OpcodeAnchor = { 0x07, 0x02, 0x06 };

    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out SummonSpawnInfo evt)
    {
        evt = null!;
        int summonId;
        int ownerId;
        int? mobCode = null;
        if (body.Length < 2) return false;
        if (body[0] != 0x40 || body[1] != 0x36) return false;

        int offset = 2;
        if (!FrameAssembler.TryReadVarInt(body[offset..], out summonId, out _)) return false;

        // Try mobCode marker A, fall back to B
        int markerIdx = ByteSearch.IndexOf(body, MobCodeMarkerA);
        if (markerIdx < 0) markerIdx = ByteSearch.IndexOf(body, MobCodeMarkerB);
        if (markerIdx >= 3)
        {
            int b1 = body[markerIdx - 3];
            int b2 = body[markerIdx - 2];
            int b3 = body[markerIdx - 1];
            mobCode = b1 | (b2 << 8) | (b3 << 16);
        }

        // Find owner key marker (8x 0xff)
        int keyIdx = ByteSearch.IndexOf(body, OwnerKeyMarker);
        if (keyIdx < 0) return false;
        int afterKey = keyIdx + OwnerKeyMarker.Length;
        if (afterKey >= body.Length) return false;

        var afterSpan = body[afterKey..];
        int opcodeIdx = ByteSearch.IndexOf(afterSpan, OpcodeAnchor);
        if (opcodeIdx < 0) return false;

        int ownerOffset = keyIdx + opcodeIdx + 11;
        if (ownerOffset + 2 > body.Length) return false;
        ownerId = body[ownerOffset] | (body[ownerOffset + 1] << 8);

        evt = new SummonSpawnInfo(summonId, ownerId, mobCode, timestampTicks, sourceIpv4);
        return true;
    }
}
