using System.Collections.Concurrent;

namespace Aion2FunDps.Core.Repositories;

/// <summary>
/// Tracks summonId → ownerId mapping. When SUMMON_SPAWN packet arrives, register here.
/// When DAMAGE event arrives with actor=summonId, attribute to owner instead.
/// </summary>
public sealed class SummonRepository
{
    private readonly ConcurrentDictionary<int, int> _summonToOwner = new();

    public void Register(int summonId, int ownerId) =>
        _summonToOwner[summonId] = ownerId;

    public int? GetOwner(int summonId) =>
        _summonToOwner.TryGetValue(summonId, out var owner) ? owner : null;

    public bool IsSummon(int actorId) =>
        _summonToOwner.ContainsKey(actorId);

    public int Count => _summonToOwner.Count;
}
