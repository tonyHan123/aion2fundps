// ViewBuilder 가 raw event 로 재구성한 player 통계 snapshot. immutable.
using Aion2FunDps.Core.Sessions;

namespace Aion2FunDps.Core.EventSourcing;

/// <summary>
/// 한 canonical player 의 집계 통계. ViewBuilder.Build 의 출력 한 항목.
///
/// 기존 <see cref="PlayerStats"/> 와 1:1 매핑. 차이.
///   - immutable. ViewBuilder 가 매 tick 새로 만듦.
///   - DPS 는 packet timestamp 기반 (wall-clock 시계 ≠ packet 시계).
///     wall-clock 호환은 BuiltAt - FirstHitTicks 간격으로 환산하는 SessionView level 에서 처리.
///   - frozen-dps 표현은 BossState.LastKilledBossId 기준으로 SessionView 가 별도 보관.
///     PlayerStatsView 본체는 raw 누적값만 보유.
/// </summary>
public sealed class PlayerStatsView
{
    public int ActorId { get; init; }
    public long TotalDamage { get; init; }
    public int HitCount { get; init; }
    public int CritCount { get; init; }
    public int BackAttackCount { get; init; }
    public int DotHitCount { get; init; }
    public long FirstHitTicks { get; init; }
    public long LastHitTicks { get; init; }

    /// <summary>target_id → 누적 데미지. 기존 PlayerStats.DamagePerTarget 와 동일.</summary>
    public IReadOnlyDictionary<int, long> DamagePerTarget { get; init; } = new Dictionary<int, long>();

    /// <summary>target_id 의 첫/마지막 hit (packet ticks). DPS 분모 계산용.</summary>
    public IReadOnlyDictionary<int, (long FirstTicks, long LastTicks)> TargetTimes { get; init; }
        = new Dictionary<int, (long, long)>();

    /// <summary>skill_code → SkillStats. Phase 2 첫 컷에선 기존 SkillStats 객체를 누적해서 재사용.
    /// Phase 3 에서 SkillStatsView 로 분리 검토.</summary>
    public IReadOnlyDictionary<uint, SkillStats> Skills { get; init; } = new Dictionary<uint, SkillStats>();

    public IReadOnlySet<int> Targets { get; init; } = new HashSet<int>();

    public double CritRate => HitCount == 0 ? 0 : (double)CritCount / HitCount;
    public double BackAttackRate => HitCount == 0 ? 0 : (double)BackAttackCount / HitCount;

    /// <summary>전체 fight DPS. fightDurationSec 는 SessionView 가 계산해서 넘김 (packet ticks 간격).</summary>
    public double DpsOver(double fightDurationSec)
    {
        if (fightDurationSec < 0.5 || HitCount < 1) return 0;
        return TotalDamage / fightDurationSec;
    }

    public double DpsToTarget(int targetId, double fightDurationSec)
    {
        if (fightDurationSec < 0.5) return 0;
        if (!DamagePerTarget.TryGetValue(targetId, out var dmg) || dmg <= 0) return 0;
        return dmg / fightDurationSec;
    }
}
