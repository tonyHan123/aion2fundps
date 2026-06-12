namespace Aion2FunDps.Core.Sessions;

public enum JobClass
{
    Unknown,
    Gladiator,    // 검성 - 양손검
    Guardian,     // 수호성 - 방패, 탱커
    Assassin,     // 살성 - 단검, 잠행
    Archer,       // 궁성 - 활
    Sorcerer,     // 마도성 - 원거리 마법
    Spiritmaster, // 정령성 - 정령 소환
    Cleric,       // 치유성 - 신성/치유
    Songweaver,   // 호법성 - 진언/버프
}

/// <summary>
/// Deterministic class detection from skill_code prefix.
///
/// Verified against the user-supplied master skill list (2026-04-29):
/// every Aion 2 KR class skill encodes its class in the high 2 digits
/// of the skill_code (code / 1_000_000).
///
///   11xxxxxx → Gladiator     15xxxxxx → Sorcerer
///   12xxxxxx → Guardian      16xxxxxx → Spiritmaster
///   13xxxxxx → Assassin      17xxxxxx → Cleric
///   14xxxxxx → Archer        18xxxxxx → Songweaver
///
/// Prefixes outside [11..18] (legacy 4-digit codes, generic flight/etc.)
/// are not class-specific → Unknown, simply not counted.
/// A handful of skills share names across classes (충격 해제 등) but each
/// class gets its own code, so this scheme attributes them correctly.
/// </summary>
public static class JobClassDetector
{
    /// <summary>Class implied by a single skill code, or Unknown.</summary>
    public static JobClass FromSkillCode(uint skillCode) =>
        (skillCode / 1_000_000u) switch
        {
            11 => JobClass.Gladiator,
            12 => JobClass.Guardian,
            13 => JobClass.Assassin,
            14 => JobClass.Archer,
            15 => JobClass.Sorcerer,
            16 => JobClass.Spiritmaster,
            17 => JobClass.Cleric,
            18 => JobClass.Songweaver,
            _  => JobClass.Unknown,
        };

    /// <summary>
    /// Maps Aion 2's network-protocol jobCode (carried in op=0297 / op=__97
    /// member records at statsOffset+0) to the meter's JobClass enum.
    /// Source: A2Viewer's JobMapping.GameToName — each in-game class has 4
    /// jobCode values covering its specializations (e.g. 13/14/15/16 all
    /// resolve to 궁성 because base ranger + 3 advanced trees).
    ///
    /// This is the authoritative class signal for the leaderboard — works
    /// at matchmaking-room entry, before any damage tick. The skill-code
    /// path (<see cref="Detect"/>) is the fallback for cases where the
    /// roster broadcast arrived without a job byte (truncated packet,
    /// unknown layout).
    /// </summary>
    public static JobClass FromGameJobCode(int jobCode) => jobCode switch
    {
        >= 5 and <= 8 => JobClass.Gladiator,
        >= 9 and <= 12 => JobClass.Guardian,
        >= 13 and <= 16 => JobClass.Archer,
        >= 17 and <= 20 => JobClass.Assassin,
        >= 21 and <= 24 => JobClass.Spiritmaster,
        >= 25 and <= 28 => JobClass.Sorcerer,
        >= 29 and <= 32 => JobClass.Cleric,
        >= 33 and <= 36 => JobClass.Songweaver,
        _ => JobClass.Unknown,
    };

    /// <summary>
    /// Returns the dominant class across the given skill codes (most casts win).
    /// Unknown if no class-typed skills observed yet.
    /// </summary>
    public static JobClass Detect(IEnumerable<uint> skillCodes)
    {
        Span<int> tally = stackalloc int[9];   // index = (int)JobClass
        foreach (var code in skillCodes)
        {
            var cls = FromSkillCode(code);
            if (cls != JobClass.Unknown)
                tally[(int)cls]++;
        }

        int bestIdx = 0, bestCount = 0;
        for (int i = 1; i < tally.Length; i++)
        {
            if (tally[i] > bestCount)
            {
                bestCount = tally[i];
                bestIdx = i;
            }
        }
        return bestCount == 0 ? JobClass.Unknown : (JobClass)bestIdx;
    }

    public static string GetKoreanName(JobClass jc) => jc switch
    {
        JobClass.Gladiator    => "검성",
        JobClass.Guardian     => "수호성",
        JobClass.Assassin     => "살성",
        JobClass.Archer       => "궁성",
        JobClass.Sorcerer     => "마도성",
        JobClass.Spiritmaster => "정령성",
        JobClass.Cleric       => "치유성",
        JobClass.Songweaver   => "호법성",
        _ => "?",
    };

    /// <summary>Single Korean character for compact display in colored badge.</summary>
    public static string GetShortChar(JobClass jc) => jc switch
    {
        JobClass.Gladiator    => "검",
        JobClass.Guardian     => "수",
        JobClass.Assassin     => "살",
        JobClass.Archer       => "궁",
        JobClass.Sorcerer     => "마",
        JobClass.Spiritmaster => "정",
        JobClass.Cleric       => "치",
        JobClass.Songweaver   => "호",
        _ => "?",
    };

    /// <summary>Hex color (string form) — UI converts to brush. Class-themed, darkened
    /// enough that white text + drop shadow stays readable on every option.</summary>
    public static string GetColorHex(JobClass jc) => jc switch
    {
        JobClass.Gladiator    => "#C0392B", // dark red
        JobClass.Guardian     => "#D35400", // dark orange
        JobClass.Assassin     => "#34495E", // dark slate
        JobClass.Archer       => "#1F8B4C", // dark green
        JobClass.Sorcerer     => "#8E44AD", // dark purple
        JobClass.Spiritmaster => "#16A085", // dark teal
        JobClass.Cleric       => "#B7950B", // deep gold (was too light)
        JobClass.Songweaver   => "#2874A6", // dark blue
        _ => "#566573",                     // dark gray
    };

    /// <summary>
    /// 앰비언트 글래스 행 막대/아이콘용 브라이트 팔레트 (다크 BG 기준).
    /// v2 (2026-06-12): 이웃 색 쌍 분리 — 살성 보라↔마도성 핑크, 수호 진주황↔치유 노랑,
    /// 정령 청록↔호법 진파랑. 디자인 결정 기록은 docs/ambient-glass-context.md.
    /// </summary>
    public static string GetBrightColorHex(JobClass jc) => jc switch
    {
        JobClass.Gladiator    => "#FF5252", // 빨강
        JobClass.Guardian     => "#FF7E29", // 진주황
        JobClass.Assassin     => "#9775FA", // 보라
        JobClass.Archer       => "#4ED44E", // 초록
        JobClass.Sorcerer     => "#F0609E", // 핑크
        JobClass.Spiritmaster => "#20C9B0", // 청록
        JobClass.Cleric       => "#FFD43B", // 노랑
        JobClass.Songweaver   => "#4D8DFF", // 진파랑
        _ => "#9AA5B2",                     // 중립 그레이
    };
}
