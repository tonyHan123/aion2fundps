using System.Collections.Concurrent;

namespace Aion2FunDps.Core;

public sealed record MobInfo(int Code, string Name, bool IsBoss);

/// <summary>
/// Static lookup of mob template codes loaded from mobs.json.
/// Note: code is the *template* code (e.g., "Goblin Warrior" = 2003456), NOT the live entity ID.
/// Live entity IDs are mapped to template codes via SummonSpawn or similar packets at runtime.
/// </summary>
public sealed class MobDatabase
{
    private readonly Dictionary<int, MobInfo> _byCode;

    public MobDatabase(IEnumerable<MobInfo> mobs)
    {
        _byCode = mobs.ToDictionary(m => m.Code);
    }

    public int Count => _byCode.Count;

    public MobInfo? GetByCode(int code) =>
        _byCode.TryGetValue(code, out var m) ? m : null;

    public bool IsBoss(int code) =>
        _byCode.TryGetValue(code, out var m) && m.IsBoss;

    public bool IsDummy(int code) =>
        _byCode.TryGetValue(code, out var m) && m.Name.Contains("샌드백");
}
