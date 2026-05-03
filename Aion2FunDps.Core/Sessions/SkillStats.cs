using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Core.Sessions;

public sealed class SkillStats
{
    public uint SkillCode { get; }
    public long TotalDamage { get; private set; }
    public int HitCount { get; private set; }
    public int CritCount { get; private set; }

    public double CritRate => HitCount == 0 ? 0 : (double)CritCount / HitCount;
    public long AverageDamage => HitCount == 0 ? 0 : TotalDamage / HitCount;

    public SkillStats(uint skillCode) { SkillCode = skillCode; }

    public void Apply(DamageEvent evt)
    {
        TotalDamage += evt.Damage;
        HitCount++;
        if (evt.IsCritical) CritCount++;
    }

    /// <summary>
    /// Folds another SkillStats's accumulated counts into this one. Used by
    /// PlayerStats.MergeFrom when an orphan stats row gets folded into its
    /// canonical row after late nickname registration.
    /// </summary>
    internal void MergeFrom(SkillStats other)
    {
        if (other == null || ReferenceEquals(other, this)) return;
        TotalDamage += other.TotalDamage;
        HitCount    += other.HitCount;
        CritCount   += other.CritCount;
    }
}
