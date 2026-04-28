using Aion2FunDps.Core.Models;
using Aion2FunDps.Core.Repositories;
using Aion2FunDps.Core.Sessions;

namespace Aion2FunDps.Core;

/// <summary>
/// Consumes IGameEvents and maintains current Session + per-player stats + nickname registry.
/// Re-attributes summon damage to its owner via SummonRepository.
/// Single-threaded — caller serializes events.
/// </summary>
public sealed class DpsAggregator
{
    private readonly NicknameRegistry _registry;
    private readonly SummonRepository _summons;
    private readonly BossTracker _boss;
    private readonly AccuracyEstimator _accuracy;

    public Session Current { get; private set; }
    public NicknameRegistry Registry => _registry;
    public SummonRepository Summons => _summons;
    public BossTracker Boss => _boss;
    public AccuracyEstimator Accuracy => _accuracy;

    public long DamageEventCount { get; private set; }
    public long DotEventCount { get; private set; }
    public long ReattributedDamageCount { get; private set; }
    public long HpEventCount { get; private set; }
    public long NicknameEventCount { get; private set; }
    public long CombatBoundaryEventCount { get; private set; }
    public long SummonSpawnEventCount { get; private set; }

    public DpsAggregator()
    {
        _registry = new NicknameRegistry();
        _summons = new SummonRepository();
        _boss = new BossTracker();
        _accuracy = new AccuracyEstimator();
        Current = new Session();
    }

    /// <summary>
    /// Pulls leak indicators from upstream subsystems and recomputes confidence.
    /// Call from UI refresh tick (e.g., once per second).
    /// </summary>
    public void RefreshAccuracy(long droppedPackets, long malformedFrames, long unknownOpcodes)
    {
        _accuracy.DroppedPackets = droppedPackets;
        _accuracy.MalformedFrames = malformedFrames;
        _accuracy.UnknownOpcodes = unknownOpcodes;

        // HP-damage cross-check on focused entity
        if (_boss.FocusedEntityId.HasValue)
        {
            int focusId = _boss.FocusedEntityId.Value;
            uint maxHp = _boss.FocusedMaxHp ?? 0;
            uint curHp = _boss.FocusedCurrentHp ?? 0;
            long hpDelta = maxHp >= curHp ? maxHp - curHp : 0;

            long damageToFocus = 0;
            foreach (var p in Current.AllPlayers)
            {
                if (p.DamagePerTarget.TryGetValue(focusId, out var d))
                    damageToFocus += d;
            }

            if (hpDelta > 0)
            {
                long leak = Math.Max(0, hpDelta - damageToFocus);
                _accuracy.HpDamageDrift = (double)leak / hpDelta;
            }
            else
            {
                _accuracy.HpDamageDrift = 0;
            }
        }

        _accuracy.Recompute();
    }

    public void OnEvent(IGameEvent evt)
    {
        switch (evt)
        {
            case DamageEvent dmg:
                {
                    int effectiveActor = _summons.GetOwner(dmg.ActorId) ?? dmg.ActorId;
                    if (effectiveActor != dmg.ActorId) ReattributedDamageCount++;
                    Current.GetOrCreate(effectiveActor).Apply(dmg);
                    _boss.OnDamage(dmg);
                    if (dmg.IsDot) DotEventCount++;
                    else DamageEventCount++;
                }
                break;

            case NicknameInfo nick:
                _registry.Register(nick);
                NicknameEventCount++;
                break;

            case CombatBoundary:
                CombatBoundaryEventCount++;
                break;

            case MobHpUpdate hp:
                _boss.OnHpUpdate(hp);
                HpEventCount++;
                break;

            case SummonSpawnInfo sp:
                _summons.Register(sp.SummonId, sp.OwnerId);
                SummonSpawnEventCount++;
                break;
        }
    }

    /// <summary>
    /// Returns players whose stats look "real" — registered via NicknameInfo, OR have
    /// enough damage/hits to not be incoming-damage noise from mobs.
    /// </summary>
    public IEnumerable<PlayerStats> LikelyPlayers(long minDamage = 50_000, int minHits = 10)
    {
        foreach (var p in Current.AllPlayers)
        {
            bool isRegistered = _registry.GetEntry(p.ActorId) != null;
            bool meetsThreshold = p.TotalDamage >= minDamage || p.HitCount >= minHits;
            if (isRegistered || meetsThreshold)
                yield return p;
        }
    }

    /// <summary>
    /// Identifies "our crew" — actors who damaged the same targets as our primary actor.
    /// In solo: returns just self.
    /// In party (boss fight): returns self + party members (same boss target).
    /// Random nearby players hitting different mobs are filtered out automatically.
    /// </summary>
    public IEnumerable<PlayerStats> OurCrew()
    {
        var primary = ResolvePrimary();
        if (primary == null) yield break;

        if (primary.Targets.Count == 0)
        {
            yield return primary;
            yield break;
        }

        foreach (var p in Current.AllPlayers)
        {
            if (p == primary || p.Targets.Overlaps(primary.Targets))
            {
                if (_registry.GetEntry(p.ActorId) != null
                    || p.TotalDamage >= 50_000
                    || p.HitCount >= 10
                    || p == primary)
                {
                    yield return p;
                }
            }
        }
    }

    public PlayerStats? ResolvePrimary()
    {
        if (_registry.SelfUserId.HasValue
            && Current.AllPlayers.FirstOrDefault(p => p.ActorId == _registry.SelfUserId.Value) is { } selfP)
            return selfP;

        // Fallback: actor with most hits
        return Current.AllPlayers.OrderByDescending(p => p.HitCount).FirstOrDefault();
    }

    public void Reset()
    {
        Current.End();
        Current = new Session();
    }
}
