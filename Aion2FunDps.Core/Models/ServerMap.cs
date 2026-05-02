namespace Aion2FunDps.Core.Models;

/// <summary>
/// Aion 2 KR server-id → server-name lookup. The 2-byte field immediately
/// before a party-member record's nameLen carries the server id; when it
/// differs from the user's own server, the in-game lobby renders the member
/// name with a bracketed short-form suffix (e.g. "릴캐[바바]"), and we
/// mirror that convention.
///
/// Ids confirmed by inspecting captured op=0297 / op=0197 broadcasts and
/// cross-referenced against A2Viewer's ServerMap (2026-04 build, recovered
/// from a decompiled bundle and re-typed by hand here so it is our own
/// implementation, not a copy).
/// </summary>
public static class ServerMap
{
    private static readonly Dictionary<int, string> _names = new()
    {
        [2001] = "이스라펠",
        [2002] = "지켈",
        [2003] = "트리니엘",
        [2004] = "루미엘",
        [2005] = "마르쿠탄",
        [2006] = "아스펠",
        [2007] = "에레슈키갈",
        [2008] = "브리트라",
        [2009] = "네몬",
        [2010] = "하달",
        [2011] = "루드라",
        [2012] = "울고른",
        [2013] = "무닌",
        [2014] = "오다르",
        [2015] = "젠카카",
        [2016] = "크로메데",
        [2017] = "콰이링",
        [2018] = "바바룽",
        [2019] = "파프니르",
        [2020] = "인드나흐",
        [2021] = "이스할겐",
        [1001] = "시엘",
        [1002] = "네자칸",
        [1003] = "바이젤",
        [1004] = "카이시넬",
        [1005] = "유스티엘",
        [1006] = "아리엘",
        [1007] = "프레기온",
        [1008] = "메스람타에다",
        [1009] = "히타니에",
        [1010] = "나니아",
        [1011] = "타하바타",
        [1012] = "루터스",
        [1013] = "페르노스",
        [1014] = "다미누",
        [1015] = "카사카",
        [1016] = "바카르마",
        [1017] = "챈가룽",
        [1018] = "코치룽",
        [1019] = "이슈타르",
        [1020] = "티아마트",
        [1021] = "포에타",
    };

    public static string? GetName(int serverId)
        => _names.TryGetValue(serverId, out var name) ? name : null;

    /// <summary>
    /// In-game lobby uses the first 2 Korean chars as the bracketed suffix
    /// (e.g. "바바룽" → "[바바]"). Returning the short form keeps the
    /// leaderboard column narrow enough to not push out the damage numbers.
    /// </summary>
    public static string? GetShortName(int serverId)
    {
        var full = GetName(serverId);
        if (string.IsNullOrEmpty(full)) return null;
        return full.Length <= 2 ? full : full[..2];
    }

    public static bool IsKnownServerId(int id) => _names.ContainsKey(id);
}
