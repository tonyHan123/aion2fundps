using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Core.Sessions;

/// <summary>
/// Tracks per-entity HP and damage event counts. Identifies current "focus" target
/// (entity receiving most damage) and flags boss mode when focus has high HP.
/// </summary>
public sealed class BossTracker
{
    private const uint BossHpThreshold = 500_000;

    private readonly Dictionary<int, EntityState> _entities = new();

    public int? FocusedEntityId { get; private set; }
    public bool IsBossMode =>
        FocusedEntityId.HasValue
        && _entities.TryGetValue(FocusedEntityId.Value, out var s)
        && s.MaxHp >= BossHpThreshold;

    public uint? FocusedMaxHp =>
        FocusedEntityId.HasValue && _entities.TryGetValue(FocusedEntityId.Value, out var s)
            ? s.MaxHp : null;

    public uint? FocusedCurrentHp =>
        FocusedEntityId.HasValue && _entities.TryGetValue(FocusedEntityId.Value, out var s)
            ? s.CurrentHp : null;

    public void OnHpUpdate(MobHpUpdate hp)
    {
        var s = GetOrCreate(hp.MobId);
        s.CurrentHp = hp.CurrentHp;
        if (hp.CurrentHp > s.MaxHp) s.MaxHp = hp.CurrentHp;
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

    public sealed class EntityState
    {
        public uint MaxHp { get; set; }
        public uint CurrentHp { get; set; }
        public int IncomingDamageEvents { get; set; }
        public long IncomingDamageTotal { get; set; }
    }
}
