using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Core.Sessions;

/// <summary>
/// Tracks per-entity HP and damage event counts. Identifies current "focus" target
/// (entity receiving most damage) and flags boss mode when focus has high HP.
/// </summary>
public sealed class BossTracker
{
    /// <summary>HP threshold to classify "boss-grade" entity (vs path mobs / field mobs).</summary>
    public uint BossHpThreshold { get; set; } = 1_000_000;

    /// <summary>Threshold to flag an entity as in "boss mode" for UI display (lower than auto-reset threshold).</summary>
    public uint BossModeDisplayThreshold { get; set; } = 500_000;

    /// <summary>
    /// Fired when a new entity is observed with HP at or above BossHpThreshold.
    /// Subscribers (e.g., DpsAggregator) can use this to auto-reset session on boss engagement.
    /// </summary>
    public event Action<int>? NewBossDetected;

    private readonly Dictionary<int, EntityState> _entities = new();

    public int? FocusedEntityId { get; private set; }
    public bool IsBossMode =>
        FocusedEntityId.HasValue
        && _entities.TryGetValue(FocusedEntityId.Value, out var s)
        && s.MaxHp >= BossModeDisplayThreshold;

    public uint? FocusedMaxHp =>
        FocusedEntityId.HasValue && _entities.TryGetValue(FocusedEntityId.Value, out var s)
            ? s.MaxHp : null;

    public uint? FocusedCurrentHp =>
        FocusedEntityId.HasValue && _entities.TryGetValue(FocusedEntityId.Value, out var s)
            ? s.CurrentHp : null;

    public void OnHpUpdate(MobHpUpdate hp)
    {
        bool isNewEntity = !_entities.ContainsKey(hp.MobId);
        var s = GetOrCreate(hp.MobId);
        s.CurrentHp = hp.CurrentHp;
        if (hp.CurrentHp > s.MaxHp) s.MaxHp = hp.CurrentHp;

        if (isNewEntity && s.MaxHp >= BossHpThreshold)
        {
            NewBossDetected?.Invoke(hp.MobId);
        }
    }

    /// <summary>Used after a session reset to re-seed boss state without losing context.</summary>
    public void RestoreEntity(int id, uint maxHp, uint currentHp)
    {
        var s = GetOrCreate(id);
        s.MaxHp = maxHp;
        s.CurrentHp = currentHp;
        s.IncomingDamageEvents = 0;
        s.IncomingDamageTotal = 0;
        UpdateFocus();
    }

    public void OnDamage(DamageEvent dmg)
    {
        var s = GetOrCreate(dmg.TargetId);
        s.IncomingDamageEvents++;
        s.IncomingDamageTotal += dmg.Damage;
        UpdateFocus();
    }

    private EntityState GetOrCreate(int id)
    {
        if (!_entities.TryGetValue(id, out var s))
        {
            s = new EntityState();
            _entities[id] = s;
        }
        return s;
    }

    private void UpdateFocus()
    {
        // Focus = entity with most incoming damage events in the current session
        int? best = null;
        int bestCount = 0;
        foreach (var (id, s) in _entities)
        {
            if (s.IncomingDamageEvents > bestCount)
            {
                bestCount = s.IncomingDamageEvents;
                best = id;
            }
        }
        FocusedEntityId = best;
    }

    public EntityState? GetEntity(int id) =>
        _entities.TryGetValue(id, out var s) ? s : null;

    public void Reset()
    {
        _entities.Clear();
        FocusedEntityId = null;
    }

    public sealed class EntityState
    {
        public uint MaxHp { get; set; }
        public uint CurrentHp { get; set; }
        public int IncomingDamageEvents { get; set; }
        public long IncomingDamageTotal { get; set; }
    }
}
