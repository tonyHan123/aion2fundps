using System.Collections.Concurrent;

namespace Aion2FunDps.Core.Repositories;

/// <summary>
/// Maps live entity IDs (per-spawn) to mob template codes (mobs.json keys).
/// Populated from SUMMON_SPAWN packets which carry mobCode.
/// Used to resolve boss names: entityId → mobCode → MobInfo.Name.
/// </summary>
public sealed class EntityRegistry
{
    private readonly ConcurrentDictionary<int, int> _entityToMobCode = new();

    public void Register(int entityId, int mobCode) =>
        _entityToMobCode[entityId] = mobCode;

    public int? GetMobCode(int entityId) =>
        _entityToMobCode.TryGetValue(entityId, out var c) ? c : null;

    public int Count => _entityToMobCode.Count;
}
