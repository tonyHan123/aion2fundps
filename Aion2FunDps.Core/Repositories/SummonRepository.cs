namespace Aion2FunDps.Core.Repositories;

/// <summary>
/// Tracks summonId → ownerId mapping. When SUMMON_SPAWN packet arrives, register here.
/// When DAMAGE event arrives with actor=summonId, attribute to owner instead.
///
/// Cap (10k entries, FIFO) bounds memory the same way as EntityRegistry —
/// summon ids accumulate over hours of play but stale ids are never queried.
/// </summary>
public sealed class SummonRepository
{
    private const int Capacity = 10_000;
    private readonly BoundedConcurrentDictionary<int, int> _summonToOwner = new(Capacity);

    public void Register(int summonId, int ownerId) =>
        _summonToOwner.Set(summonId, ownerId);

    public int? GetOwner(int summonId) =>
        _summonToOwner.TryGetValue(summonId, out var owner) ? owner : null;

    public bool IsSummon(int actorId) =>
        _summonToOwner.ContainsKey(actorId);

    public int Count => _summonToOwner.Count;
}
