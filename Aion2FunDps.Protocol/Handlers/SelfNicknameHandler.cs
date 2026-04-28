using System.Text;
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Protocol.Handlers;

/// <summary>
/// Parses opcode 0x33 0x36 — self-character nickname info.
/// Layout: [opcode] [userId varint] [10-byte search for 0x07 spliter]
///         [nameLength varint] [name UTF-8] [server uint16 LE] [job byte]
/// </summary>
public static class SelfNicknameHandler
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        long timestampTicks,
        uint sourceIpv4,
        out NicknameInfo evt)
    {
        evt = null!;
        if (body.Length < 2) return false;
        if (body[0] != 0x33 || body[1] != 0x36) return false;

        int offset = 2;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int userId, out int n)) return false;
        offset += n;

        // Search next 10 bytes for 0x07 spliter
        if (offset + 10 > body.Length) return false;
        int spliterIdx = -1;
        for (int i = 0; i < 10; i++)
        {
            if (body[offset + i] == 0x07) { spliterIdx = i; break; }
        }
        if (spliterIdx < 0) return false;
        offset += spliterIdx + 1;

        if (!FrameAssembler.TryReadVarInt(body[offset..], out int nameLength, out n)) return false;
        offset += n;
        if (nameLength <= 0 || nameLength > 71) return false;
        if (offset + nameLength > body.Length) return false;

        string nickname;
        try
        {
            nickname = Encoding.UTF8.GetString(body.Slice(offset, nameLength));
        }
        catch
        {
            return false;
        }
        if (!IsValidNickname(nickname)) return false;
        offset += nameLength;

        int server = -1;
        int job = -1;
        if (offset + 2 <= body.Length)
        {
            server = body[offset] | (body[offset + 1] << 8);
            offset += 2;
            if (offset < body.Length) job = body[offset];
        }

        evt = new NicknameInfo(userId, nickname, IsSelf: true, server, job, timestampTicks, sourceIpv4);
        return true;
    }

    public static bool IsValidNickname(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s.Length > 30) return false;
        foreach (char c in s)
        {
            if (char.IsControl(c)) return false;
        }
        return true;
    }
}
