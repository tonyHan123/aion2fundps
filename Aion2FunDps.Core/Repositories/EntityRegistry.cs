namespace Aion2FunDps.Core.Repositories;

/// <summary>
/// Maps live entity IDs (per-spawn) to mob template codes (mobs.json keys).
/// Populated from SUMMON_SPAWN packets which carry mobCode.
/// Used to resolve boss names: entityId → mobCode → MobInfo.Name.
///
/// Cap (10k entries, FIFO eviction) bounds memory in long sessions where
/// each spawn produces a new entity id — capital cities and busy zones
/// generate thousands per hour. Stale ids never queried after despawn so
/// FIFO is appropriate.
/// </summary>
public sealed class EntityRegistry
{
    private const int Capacity = 10_000;
    private readonly BoundedConcurrentDictionary<int, int> _entityToMobCode = new(Capacity);

    public void Register(int entityId, int mobCode) =>
        _entityToMobCode.Set(entityId, mobCode);

    public int? GetMobCode(int entityId) =>
        _entityToMobCode.TryGetValue(entityId, out var c) ? c : null;

    public int Count => _entityToMobCode.Count;
}
