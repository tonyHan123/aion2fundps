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
/// Heuristic class detection from skill names. Aion 2 skill names follow Korean class
/// naming conventions reliably enough for this approach. Returns Unknown if no match.
/// </summary>
public static class JobClassDetector
{
    private static readonly (JobClass Class, string[] Keywords)[] KeywordRules = new[]
    {
        (JobClass.Gladiator,    new[] { "내려찍기", "올려치기", "맹타", "광폭", "격노", "유린", "분노", "회전 베기", "쇄도", "베기" }),
        (JobClass.Guardian,     new[] { "방패", "수호", "도발", "방어 태세", "처단의 일격", "성스러운", "시련의" }),
        (JobClass.Assassin,     new[] { "단검", "암습", "그림자", "은신", "치명 가격", "급습", "치사", "독" }),
        (JobClass.Archer,       new[] { "화살", "사격", "올가미", "속사", "조준", "관통", "연사" }),
        (JobClass.Sorcerer,     new[] { "화염", "냉기", "번개", "메테오", "마력", "원소", "폭발", "빙결", "마법" }),
        (JobClass.Spiritmaster, new[] { "정령", "소환", "원혼", "영혼", "분신" }),
        (JobClass.Cleric,       new[] { "치유", "회복", "신성", "축복", "정화", "재림", "부활", "생명" }),
        (JobClass.Songweaver,   new[] { "진언", "침묵", "법구", "노래", "선율", "공명", "안식", "고무", "찬가" }),
    };

    public static JobClass Detect(IEnumerable<uint> skillCodes, SkillDatabase skills)
    {
        var counts = new Dictionary<JobClass, int>();
        int matchedCount = 0;

        foreach (var code in skillCodes)
        {
            var info = skills.Resolve((int)code);
            if (info == null) continue;

            foreach (var (cls, kws) in KeywordRules)
            {
                foreach (var kw in kws)
                {
                    if (info.Name.Contains(kw))
                    {
                        counts[cls] = counts.GetValueOrDefault(cls) + 1;
                        matchedCount++;
                        break;
                    }
                }
            }
        }

        if (matchedCount == 0) return JobClass.Unknown;
        return counts.OrderByDescending(kv => kv.Value).First().Key;
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
}
