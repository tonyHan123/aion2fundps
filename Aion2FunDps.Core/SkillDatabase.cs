namespace Aion2FunDps.Core;

public sealed record SkillInfo(int Code, string Name);

public sealed class SkillDatabase
{
    private readonly Dictionary<int, SkillInfo> _byCode;

    public SkillDatabase(IEnumerable<SkillInfo> skills)
    {
        _byCode = skills.ToDictionary(s => s.Code);
    }

    public int Count => _byCode.Count;

    public SkillInfo? GetByCode(int code) =>
        _byCode.TryGetValue(code, out var s) ? s : null;

    /// <summary>
    /// Tries exact code, then code/10, then code/100. Matches TK fallback strategy.
    /// </summary>
    public SkillInfo? Resolve(int code)
    {
        if (_byCode.TryGetValue(code, out var exact)) return exact;
        if (_byCode.TryGetValue((code / 10) * 10, out var ten)) return ten;
        if (_byCode.TryGetValue((code / 100) * 100, out var hundred)) return hundred;
        return null;
    }
}
