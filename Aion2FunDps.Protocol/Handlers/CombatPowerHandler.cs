using System.Text;
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x00 0x92 — dedicated combat-power broadcast. Ported from
/// A2Viewer's PartyStreamParser.ScanCombatPowerRaw (May 2026 RE).
///
/// Layout: the packet contains a stats trailer ending in the magic bytes
/// <c>06 00 36</c>, with a 5-byte zero pad immediately before it. Reading
/// backward from the magic:
///   num-21..-18  level (4 LE, must be 1..55)
///   num-17..-14  zero (must be 0)
///   num-13..-10  item level (4 LE, must be 1000..5000) — sanity gate
///   num-9..-6    CP (4 LE, must be 10_000..999_999)
///   num-5..-1    zero pad (5 bytes)
///   num..num+2   magic 06 00 36
/// The nickname comes earlier in the packet as
/// <c>[server 2 LE][nameLen 1][UTF-8 nickname]</c> — the parser scans the
/// bytes preceding the stats trailer for a plausible (server, name) pair.
///
/// This handler does NOT carry an entity_id, so it emits
/// <see cref="CombatPowerUpdate"/> (name-keyed) instead of
/// <see cref="NicknameInfo"/> (id-keyed). The aggregator resolves the name
/// against the existing registry to update CP without creating phantom rows.
/// </summary>
public static class CombatPowerHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out CombatPowerUpdate evt)
    {
        evt = null!;
        if (body.Length < 30) return false;
        if (body[0] != 0x00 || body[1] != 0x92) return false;

        // Scan from end backward for the magic 06 00 36 anchor.
        for (int num = body.Length - 3; num >= 21; num--)
        {
            if (body[num] != 0x06 || body[num + 1] != 0x00 || body[num + 2] != 0x36)
                continue;

            // 5-byte zero pad immediately preceding the magic.
            bool zeros = true;
            for (int j = num - 5; j < num; j++)
            {
                if (body[j] != 0) { zeros = false; break; }
            }
            if (!zeros) continue;

            int cp = ReadInt32LE(body, num - 9);
            if (cp < 10_000 || cp > 999_999) continue;

            int itemLevel = ReadInt32LE(body, num - 13);
            if (itemLevel < 1000 || itemLevel > 5000) continue;

            int zeroField = ReadInt32LE(body, num - 17);
            if (zeroField != 0) continue;

            int level = ReadInt32LE(body, num - 21);
            if (level < 1 || level > 55) continue;

            // Now scan from the start of the packet for [server 2][nameLen 1][nickname]
            // that ends before the stats trailer.
            int nameScanLimit = num - 21;
            for (int k = 0; k + 3 < nameScanLimit; k++)
            {
                int server = body[k] | (body[k + 1] << 8);
                if (server < 1001 || server > 2021) continue;

                int nameLen = body[k + 2];
                if (nameLen < 3 || nameLen > 48) continue;
                if (k + 3 + nameLen > nameScanLimit) continue;

                string nickname;
                try
                {
                    nickname = Encoding.UTF8.GetString(body.Slice(k + 3, nameLen));
                }
                catch { continue; }

                if (!IsValidNickname(nickname)) continue;

                evt = new CombatPowerUpdate(
                    Nickname: nickname,
                    ServerId: server,
                    CombatPower: cp,
                    TimestampTicks: timestampTicks,
                    SourceIpv4: sourceIpv4);
                return true;
            }
        }
        return false;
    }

    private static int ReadInt32LE(ReadOnlySpan<byte> body, int offset) =>
        body[offset] | (body[offset + 1] << 8) | (body[offset + 2] << 16) | (body[offset + 3] << 24);

    private static bool IsValidNickname(string nick)
    {
        if (string.IsNullOrEmpty(nick) || nick.Length < 2) return false;
        foreach (var c in nick)
        {
            bool ok = (c >= '가' && c <= '힣')
                  || (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9');
            if (!ok) return false;
        }
        return true;
    }
}
