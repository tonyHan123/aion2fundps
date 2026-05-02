using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x01 0x91 — periodic encounter banner / boss intro packet.
///
/// Two layouts observed (reverse-engineered 2026-04-29 across two test sessions):
///
///   Layout A (1-boss, len=31):
///     [opcode 2B] [pad 2B] [hdr 5B] [count=01] [prefix 3B] [mobCode 4B LE] [tail 14B]
///
///   Layout B (2-boss, len=48):
///     [opcode 2B] [pad 2B] [hdr 5B] [count=02] [boss block × N] [tail 2B]
///         per-boss block (18B): [varint prefix ~2B] [mobCode 4B LE] [coords 12B]
///
/// Both layouts share the same header up to the count byte at offset 9.
/// After that, the per-boss block has a variable-length VARINT prefix
/// (looks like an entity-id), then 4-byte LE mob_code, then 12 bytes of coords.
/// We don't need anything but the FIRST mob_code, so we walk one varint past the
/// count byte, then read the mob_code 4 bytes after it.
/// </summary>
public static class EncounterAnnounceHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out EncounterAnnouncement evt)
    {
        evt = null!;
        if (body.Length < 16) return false;
        if (body[0] != 0x01 || body[1] != 0x91) return false;

        // Header is fixed: 2 (opcode) + 2 (pad) + 5 (hdr) + 1 (count) = 10 bytes.
        // Per-boss block starts at offset 10 with a VarInt prefix (entityId-like
        // hash) of 1..3 bytes, immediately followed by the 4-byte LE mob_code.
        if (!TryReadVarInt(body, 10, out _, out int prefixLen))
            return false;

        int mobCodeOffset = 10 + prefixLen;
        if (mobCodeOffset + 4 > body.Length) return false;

        int mobCode = body[mobCodeOffset]
                    | (body[mobCodeOffset + 1] << 8)
                    | (body[mobCodeOffset + 2] << 16)
                    | (body[mobCodeOffset + 3] << 24);

        // Sanity gate: real mob_codes in mobs.json are 1M..30M. Reject corruption /
        // opcode confusion / wrong-offset reads (e.g., the legacy 4-digit "9028"
        // we used to extract before this fix).
        if (mobCode < 1_000_000 || mobCode > 50_000_000) return false;

        evt = new EncounterAnnouncement(mobCode, timestampTicks, sourceIpv4);
        return true;
    }

    /// <summary>
    /// Inline VarInt reader (protobuf-style, 7 bits per byte, MSB=continue).
    /// Returns false on overflow or buffer underflow. We don't want a hard
    /// dependency on FrameAssembler here.
    /// </summary>
    private static bool TryReadVarInt(ReadOnlySpan<byte> buf, int offset, out int value, out int length)
    {
        value = 0;
        length = 0;
        int shift = 0;
        while (offset + length < buf.Length && length < 5)
        {
            byte b = buf[offset + length];
            length++;
            value |= (b & 0x7f) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
