using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Core.Sessions;

public sealed class PlayerStats
{
    public int ActorId { get; }
    public long TotalDamage { get; private set; }
    public int HitCount { get; private set; }
    public int CritCount { get; private set; }
    public int BackAttackCount { get; private set; }
    public int DotHitCount { get; private set; }
    public long FirstHitTicks { get; private set; }
    public long LastHitTicks { get; private set; }

    private readonly Dictionary<uint, SkillStats> _skills = new();
    public IReadOnlyDictionary<uint, SkillStats> Skills => _skills;

    private readonly HashSet<int> _targets = new();
    public IReadOnlySet<int> Targets => _targets;

    private readonly Dictionary<int, long> _damagePerTarget = new();
    public IReadOnlyDictionary<int, long> DamagePerTarget => _damagePerTarget;

    public PlayerStats(int actorId) { ActorId = actorId; }

    public void Apply(DamageEvent evt)
    {
        if (HitCount == 0) FirstHitTicks = evt.TimestampTicks;
        LastHitTicks = evt.TimestampTicks;

        TotalDamage += evt.Damage;
        HitCount++;
        if (evt.IsCritical) CritCount++;
        if (evt.IsBackAttack) BackAttackCount++;
        if (evt.IsDot) DotHitCount++;

        _targets.Add(evt.TargetId);
        _damagePerTarget[evt.TargetId] =
            _damagePerTarget.GetValueOrDefault(evt.TargetId, 0) + evt.Damage;

        if (!_skills.TryGetValue(evt.SkillCode, out var s))
        {
            s = new SkillStats(evt.SkillCode);
            _skills[evt.SkillCode] = s;
        }
        s.Apply(evt);
    }

    public double Dps
    {
        get
        {
            if (HitCount < 2) return 0;
            double seconds = (LastHitTicks - FirstHitTicks) / (double)TimeSpan.TicksPerSecond;
            return seconds <= 0 ? 0 : TotalDamage / seconds;
        }
    }

    public double CritRate => HitCount == 0 ? 0 : (double)CritCount / HitCount;
    public double BackAttackRate => HitCount == 0 ? 0 : (double)BackAttackCount / HitCount;

    /// <summary>True if this actor and another share at least one target.</summary>
    public bool SharesTargetWith(PlayerStats other)
    {
        if (other == this) return true;
        if (_targets.Count == 0 || other._targets.Count == 0) return false;
        return _targets.Overlaps(other._targets);
    }
}
