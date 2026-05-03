using System.Collections.Concurrent;
using System.Reflection;

namespace Aion2FunDps.Core.Sessions;

/// <summary>
/// Resolves skill_code → skill name and skill_code → CDN icon URL.
///
/// Two embedded data sources, both MIT-licensed:
///   - skills_tk.json (8211 entries): code → name catalog from
///     TK-open-public/Aion2-Dps-Meter, used by GetName.
///   - skill_icon_overrides.json (375 entries): explicit code/10000 → ICON
///     filename mapping from taengu/AION2-DPS-Meter, used by GetIconUrl as
///     the primary lookup before falling back to the algorithmic formula.
///
/// All other icon URLs are computed deterministically from the skill code
/// using taengu's algorithm (theostones via hex pattern, class skills via
/// 2-letter prefix), so the catalog files cover the long tail and the
/// algorithm covers everything else. Misses fall back to "#code" for names
/// and null for icons (caller renders class icon instead).
/// </summary>
public static class SkillCatalog
{
    private static readonly Lazy<ConcurrentDictionary<uint, string>> _byCode =
        new(LoadTkSkillsJson);

    public static string GetName(uint skillCode)
    {
        if (_byCode.Value.TryGetValue(skillCode, out var name))
            return name;
        return $"#{skillCode}";
    }

    private const string CdnBase = "https://assets.playnccdn.com/static-aion2-gamedata/resources";

    /// <summary>
    /// Returns the NCSoft CDN icon URL for a skill code. Algorithm ported
    /// from taengu/AION2-DPS-Meter (src/main/resources/js/skillIcons.js):
    ///  1) Theostones (신석, codes starting with "30") → computed via the
    ///     Icon_Item_Usable_Godstone_WP_r_{hex}.png pattern using bytes 5-6
    ///     of the padded 8-digit code as the icon index.
    ///  2) Class skills with an explicit override in skill_icon_overrides.json
    ///     (taengu's curated 375-entry table keyed by `code/10000`).
    ///  3) Algorithmic fallback for class skills 11-18xxxxxx → ICON_{prefix}_
    ///     SKILL_{sub:003}.png where prefix = GL/TE/AS/RA/SO/EL/CL/CH and
    ///     sub = digits 2-3 of the 8-digit code.
    /// Returns null when no rule matches (caller falls back to class icon).
    /// </summary>
    public static string? GetIconUrl(uint skillCode)
    {
        // Pad to canonical 8-digit form. Taengu does this because Aion 2 KR
        // emits both short legacy codes (4-digit) and modern long codes;
        // the algorithm slices fixed offsets so the input must be normalized.
        string code = skillCode.ToString();
        if (code.Length < 8) code = code.PadRight(8, '0');
        else if (code.Length > 8) code = code.Substring(0, 8);

        // (1) Theostone / 신석. codes 3xxxxxxx with at least 7 meaningful
        // digits. Quality nibble at position 4, icon code at 5-6 (decimal),
        // converted to 3-digit hex for the file name.
        if (code.StartsWith("30"))
        {
            if (int.TryParse(code.AsSpan(5, 2), out int iconCode) && iconCode > 0)
            {
                string iconHex = iconCode.ToString("x").PadLeft(3, '0');
                return $"{CdnBase}/Icon_Item_Usable_Godstone_WP_r_{iconHex}.png";
            }
        }

        // (2) Explicit override table (taengu's curated map, keyed by first
        // 4 digits = skill family). Wins over algorithmic guess because the
        // game's icon naming has many one-off mismatches the formula misses.
        string base4 = code.Substring(0, 4);
        if (_iconOverrides.Value.TryGetValue(base4, out var iconName))
            return $"{CdnBase}/{iconName}.png";

        // (3) Algorithmic fallback for class skill codes 11-18xxxxxx.
        // Class prefix → 2-letter NCSoft code; sub-id is digits [2..3].
        string? cls = code.Substring(0, 2) switch
        {
            "11" => "GL", // Gladiator (검성)
            "12" => "TE", // Templar / Guardian (수호성)
            "13" => "AS", // Assassin (살성)
            "14" => "RA", // Ranger / Archer (궁성)
            "15" => "SO", // Sorcerer (마도성)
            "16" => "EL", // Elementalist / Spiritmaster (정령성)
            "17" => "CL", // Cleric (치유성)
            "18" => "CH", // Chanter / Songweaver (호법성)
            _    => null,
        };
        if (cls != null && int.TryParse(code.AsSpan(2, 2), out int sub))
        {
            return $"{CdnBase}/ICON_{cls}_SKILL_{sub:000}.png";
        }

        return null;
    }

    private static readonly Lazy<Dictionary<string, string>> _iconOverrides =
        new(LoadIconOverrides);

    private static Dictionary<string, string> LoadIconOverrides()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var asm = typeof(SkillCatalog).Assembly;
        var resourceName = $"{asm.GetName().Name}.Resources.skill_icon_overrides.json";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return dict;

        // Format: { "1009": "ICON_EL_SKILL_009", "1101": "ICON_GL_SKILL_001", ... }
        // Manual scan instead of System.Text.Json — keeps Core dependency-free.
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            int q1 = line.IndexOf('"');
            if (q1 < 0) continue;
            int q2 = line.IndexOf('"', q1 + 1);
            if (q2 < 0) continue;
            int q3 = line.IndexOf('"', q2 + 1);
            if (q3 < 0) continue;
            int q4 = line.IndexOf('"', q3 + 1);
            if (q4 < 0) continue;
            string key = line.Substring(q1 + 1, q2 - q1 - 1);
            string val = line.Substring(q3 + 1, q4 - q3 - 1);
            if (key.Length > 0 && val.Length > 0) dict[key] = val;
        }
        return dict;
    }

    private static ConcurrentDictionary<uint, string> LoadTkSkillsJson()
    {
        var dict = new ConcurrentDictionary<uint, string>();
        var asm = typeof(SkillCatalog).Assembly;
        var resourceName = $"{asm.GetName().Name}.Resources.skills_tk.json";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return dict;

        // Flat list of {"code": N, "name": "..."} objects. Manual parse
        // saves pulling System.Text.Json into Core for a one-time load.
        using var reader = new StreamReader(stream);
        string? line;
        uint? pendingCode = null;
        while ((line = reader.ReadLine()) != null)
        {
            int codeIdx = line.IndexOf("\"code\":");
            if (codeIdx >= 0)
            {
                pendingCode = ExtractJsonNumber(line, codeIdx + 7);
                continue;
            }
            int nameIdx = line.IndexOf("\"name\":");
            if (nameIdx >= 0 && pendingCode is uint c)
            {
                var name = ExtractJsonString(line, nameIdx + 7);
                if (!string.IsNullOrWhiteSpace(name))
                    dict[c] = name!;
                pendingCode = null;
            }
        }
        return dict;
    }

    private static string? ExtractJsonString(string line, int from)
    {
        int q1 = line.IndexOf('"', from);
        if (q1 < 0) return null;
        int q2 = line.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return line.Substring(q1 + 1, q2 - q1 - 1);
    }

    private static uint? ExtractJsonNumber(string line, int from)
    {
        int i = from;
        while (i < line.Length && (line[i] == ' ' || line[i] == ':')) i++;
        int start = i;
        while (i < line.Length && (char.IsDigit(line[i]))) i++;
        if (i == start) return null;
        if (uint.TryParse(line.AsSpan(start, i - start), out uint v)) return v;
        return null;
    }
}
