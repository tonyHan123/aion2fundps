using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x21 0x8d — combat start/end signal (per-mob).
/// Layout: [opcode] [mobId varint]
/// TK marks combat start/end based on whether a known boss is engaged. We emit always
/// and let the aggregator decide what to do.
/// </summary>
public static class CombatBoundaryHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out CombatBoundary evt)
    {
        evt = null!;
        if (body.Length < 2) return false;
        if (body[0] != 0x21 || body[1] != 0x8d) return false;

        int offset = 2;
        if (!FrameAssembler.TryReadVarInt(body[offset..], out int mobId, out _))
            return false;

        evt = new CombatBoundary(mobId, timestampTicks, sourceIpv4);
        return true;
    }
}
