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
    public long FirstHitTicks { get; private set; }   // packet timestamp ticks (pcap basis)
    public long LastHitTicks { get; private set; }
    private DateTime? _firstHitAt;                    // wall-clock anchor, used for rolling DPS denominator
    private double? _frozenDps;                       // when set, Dps returns this verbatim (e.g. after boss kill)
    private readonly Dictionary<int, TargetDpsState> _targetDps = new();
    private readonly Dictionary<int, double> _frozenTargetDps = new();

    private readonly Dictionary<uint, SkillStats> _skills = new();
    public IReadOnlyDictionary<uint, SkillStats> Skills => _skills;

    private readonly HashSet<int> _targets = new();
    public IReadOnlySet<int> Targets => _targets;

    private readonly Dictionary<int, long> _damagePerTarget = new();
    public IReadOnlyDictionary<int, long> DamagePerTarget => _damagePerTarget;

    public PlayerStats(int actorId) { ActorId = actorId; }

    public void Apply(DamageEvent evt)
    {
        // Note: we deliberately do NOT clear _frozenDps / _frozenTargetDps here.
        // After BossKilled freezes the values, trailing damage events (DoT ticks,
        // multi-hit followups, late packets) would otherwise immediately unfreeze
        // and let DPS decay back down — users want to see the kill-moment final
        // numbers stable until the next session reset. ResetCore creates a fresh
        // Session whose new PlayerStats start with no freeze, so cleanup is automatic.
        var now = DateTime.UtcNow;

        if (HitCount == 0)
        {
            FirstHitTicks = evt.TimestampTicks;
            _firstHitAt = now;
        }
        LastHitTicks = evt.TimestampTicks;

        TotalDamage += evt.Damage;
        HitCount++;
        if (evt.IsCritical) CritCount++;
        if (evt.IsBackAttack) BackAttackCount++;
        if (evt.IsDot) DotHitCount++;

        _targets.Add(evt.TargetId);
        _damagePerTarget[evt.TargetId] =
            _damagePerTarget.GetValueOrDefault(evt.TargetId, 0) + evt.Damage;
        if (!_targetDps.TryGetValue(evt.TargetId, out var targetState))
        {
            targetState = new TargetDpsState(now);
            _targetDps[evt.TargetId] = targetState;
        }
        targetState.LastHitAt = now;

        if (!_skills.TryGetValue(evt.SkillCode, out var s))
        {
            s = new SkillStats(evt.SkillCode);
            _skills[evt.SkillCode] = s;
        }
        s.Apply(evt);
    }

    /// <summary>
    /// Rolling DPS during combat: total damage / elapsed wall-clock since first hit.
    /// Decays when the player stops hitting (matches user intuition).
    /// Once <see cref="FreezeDps"/> is called (e.g. on boss kill), the snapshot value
    /// is returned verbatim until the player resumes attacking, at which point Apply()
    /// clears the freeze and rolling computation resumes.
    /// </summary>
    public double Dps
    {
        get
        {
            if (_frozenDps.HasValue) return _frozenDps.Value;
            if (HitCount < 1 || _firstHitAt is null) return 0;
            double seconds = (DateTime.UtcNow - _firstHitAt.Value).TotalSeconds;
            return seconds < 0.5 ? 0 : TotalDamage / seconds;
        }
    }

    public long GetDamageToTarget(int targetId) =>
        _damagePerTarget.GetValueOrDefault(targetId, 0);

    public double GetDpsToTarget(int targetId)
    {
        if (_frozenTargetDps.TryGetValue(targetId, out var frozen))
            return frozen;

        if (!_damagePerTarget.TryGetValue(targetId, out var damage) || damage <= 0)
            return 0;

        if (!_targetDps.TryGetValue(targetId, out var targetState))
            return 0;

        double seconds = (DateTime.UtcNow - targetState.FirstHitAt).TotalSeconds;
        return seconds < 0.5 ? 0 : damage / seconds;
    }

    /// <summary>
    /// Captures the current rolling DPS as a frozen value. Subsequent reads of
    /// <see cref="Dps"/> return this snapshot until the next damage event lands.
    /// Called by <see cref="DpsAggregator"/> when a boss-grade entity's HP hits 0,
    /// so the displayed DPS reflects the kill moment instead of decaying to zero.
    /// </summary>
    public void FreezeDps()
    {
        if (HitCount < 1 || _firstHitAt is null) { _frozenDps = 0; return; }
        double seconds = (DateTime.UtcNow - _firstHitAt.Value).TotalSeconds;
        _frozenDps = seconds < 0.5 ? 0 : TotalDamage / seconds;
    }

    public void FreezeDpsToTarget(int targetId)
    {
        if (!_damagePerTarget.TryGetValue(targetId, out var damage) || damage <= 0)
        {
            _frozenTargetDps[targetId] = 0;
            return;
        }

        if (!_targetDps.TryGetValue(targetId, out var targetState))
        {
            _frozenTargetDps[targetId] = 0;
            return;
        }

        double seconds = (DateTime.UtcNow - targetState.FirstHitAt).TotalSeconds;
        _frozenTargetDps[targetId] = seconds < 0.5 ? 0 : damage / seconds;
    }

    public double CritRate => HitCount == 0 ? 0 : (double)CritCount / HitCount;
    public double BackAttackRate => HitCount == 0 ? 0 : (double)BackAttackCount / HitCount;

    /// <summary>
    /// True when this actor's hits are predominantly from player-class skills
    /// (skill_code prefix 11..18). Filters out NPCs / boss adds / unmapped summons
    /// whose skills use generic 7-digit codes (prefix 1) and shouldn't appear on
    /// the leaderboard. Requires at least 5 hits to avoid premature classification.
    /// </summary>
    public bool LooksLikePlayer
    {
        get
        {
            int playerHits = 0;
            int totalHits = 0;
            foreach (var s in _skills.Values)
            {
                totalHits += s.HitCount;
                if (JobClassDetector.FromSkillCode(s.SkillCode) != JobClass.Unknown)
                    playerHits += s.HitCount;
            }
            return totalHits >= 5 && (double)playerHits / totalHits >= 0.5;
        }
    }

    /// <summary>True if this actor and another share at least one target.</summary>
    public bool SharesTargetWith(PlayerStats other)
    {
        if (other == this) return true;
        if (_targets.Count == 0 || other._targets.Count == 0) return false;
        return _targets.Overlaps(other._targets);
    }

    private sealed class TargetDpsState(DateTime firstHitAt)
    {
        public DateTime FirstHitAt { get; } = firstHitAt;
        public DateTime LastHitAt { get; set; } = firstHitAt;
    }
}
