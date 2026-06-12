// 순수 함수 f(RawEventStore, AliasLog, BossTracker, registries) → SessionView. mutate 없음.
using Aion2FunDps.Core.Models;
using Aion2FunDps.Core.Repositories;
using Aion2FunDps.Core.Sessions;

namespace Aion2FunDps.Core.EventSourcing;

/// <summary>
/// Event sourcing 의 view builder. 매 tick 호출 (현재 풀빌드 O(n)).
/// Phase 5 에서 IncrementalViewCache 가 이 함수의 캐시 버전을 둠 — 시그니처 동일하게 유지.
///
/// 입력 같으면 출력 같음 (BuiltAt 제외 — wall-clock). 테스트 / replay 에 그대로 사용.
///
/// Phase 3 변경. NicknameRegistry / SummonRepository / EntityRegistry 를 입력으로 추가 —
/// mob / friendly / 비-player 데미지 차단 (DpsAggregator.IsPlayerDamage 와 동일 게이트).
/// 이 세 컴포넌트가 mutable 이지만 시점 snapshot 으로 받아서 view 빌드.
/// </summary>
public static class ViewBuilder
{
    public static SessionView Build(
        RawEventStore events,
        AliasLog aliases,
        BossTracker boss,
        NicknameRegistry registry,
        SummonRepository summons,
        EntityRegistry entities)
    {
        var accumulators = new Dictionary<int, Accumulator>();

        foreach (var ev in events.All)
        {
            switch (ev)
            {
                case RawDamage d:
                    {
                        int effectiveActor = summons.GetOwner(d.RawActorId) ?? d.RawActorId;
                        if (!IsPlayerDamage(d.RawActorId, effectiveActor, d.TargetId, d.SkillCode, registry, summons, entities)) break;
                        int canonical = aliases.Resolve(effectiveActor, d.Timestamp);
                        GetAcc(accumulators, canonical).ApplyDamage(d);
                        break;
                    }
                case RawDotTick dot:
                    {
                        int effectiveActor = summons.GetOwner(dot.RawActorId) ?? dot.RawActorId;
                        if (!IsPlayerDamage(dot.RawActorId, effectiveActor, dot.TargetId, dot.SkillCode, registry, summons, entities)) break;
                        int canonical = aliases.Resolve(effectiveActor, dot.Timestamp);
                        GetAcc(accumulators, canonical).ApplyDot(dot);
                        break;
                    }
                // nickname / hp / summon / cp / boss 이벤트는 view 의 PlayerStats 차원에 직접
                // 입력되지 않음. nickname/server/cp 등 메타데이터는 NicknameRegistry 가 보유 —
                // UI 가 view + registry 를 함께 읽음. Phase 4 에서 registry 도 view 일부로
                // 흡수 검토.
            }
        }

        var stats = new Dictionary<int, PlayerStatsView>(accumulators.Count);
        long totalDamage = 0;
        foreach (var (id, acc) in accumulators)
        {
            stats[id] = acc.Build();
            totalDamage += acc.TotalDamage;
        }

        return new SessionView
        {
            PlayerStats = stats,
            BossState = BuildBossState(boss),
            TotalDamage = totalDamage,
            TotalEventsApplied = events.TotalCount,
            BuiltAt = DateTime.UtcNow,
        };
    }

    /// <summary>DpsAggregator.IsPlayerDamage 와 동일 분류 로직. mob / heal / friendly / pvp 차단.
    /// PlayerStats.LooksLikePlayer (행동 누적 신호) 는 view 가 보유 안 함 — single-hit 의
    /// JobClassDetector fallback 로 player class 신호만 사용. fight 초반의 self / 짭호 같이
    /// nickname 미등록인 actor 의 첫 hit 부터 통과.</summary>
    private static bool IsPlayerDamage(
        int rawActor, int effectiveActor, int targetId, uint skillCode,
        NicknameRegistry registry, SummonRepository summons, EntityRegistry entities)
    {
        bool targetIsRegistered = registry.GetEntry(targetId) != null;
        bool targetIsKnownMob = entities.GetMobCode(targetId).HasValue;
        if ((targetIsRegistered && !targetIsKnownMob) || registry.SelfUserId == targetId) return false;

        if (entities.GetMobCode(rawActor).HasValue)
            return summons.GetOwner(rawActor).HasValue;

        if (registry.GetEntry(effectiveActor) != null) return true;
        if (summons.IsSummon(rawActor) && registry.GetEntry(effectiveActor) != null) return true;
        return JobClassDetector.FromSkillCode(skillCode) != JobClass.Unknown;
    }

    private static Accumulator GetAcc(Dictionary<int, Accumulator> dict, int canonical)
    {
        if (!dict.TryGetValue(canonical, out var acc))
        {
            acc = new Accumulator(canonical);
            dict[canonical] = acc;
        }
        return acc;
    }

    private static BossStateView BuildBossState(BossTracker boss)
    {
        int? focus = boss.FocusedEntityId;
        long curHp = 0, maxHp = 0;
        if (focus.HasValue)
        {
            var entity = boss.GetEntity(focus.Value);
            if (entity != null)
            {
                curHp = entity.CurrentHp;
                maxHp = entity.MaxHp;
            }
        }
        return new BossStateView
        {
            FocusedEntityId = focus,
            IsBossMode = boss.IsBossMode,
            FocusedCurrentHp = curHp,
            FocusedMaxHp = maxHp,
            // FocusedMobCode / LastKilledBossId / FrozenTotalPartyDamage 는 호출자 (DpsAggregator)
            // 가 wrap 한 BossSnapshot 에 있음. ViewBuilder 는 BossTracker 만 받으므로 일단 비움.
            // Phase 2 의 다음 단계에서 SessionView 가 BossSnapshot 도 받도록 확장.
            FocusedMobCode = null,
            LastKilledBossId = null,
            FrozenTotalPartyDamage = null,
        };
    }

    /// <summary>per-canonical 누적기. ViewBuilder 내부 전용 — PlayerStats 와 동등한 표면을 모음.</summary>
    private sealed class Accumulator
    {
        public int ActorId { get; }
        public long TotalDamage { get; private set; }
        public int HitCount { get; private set; }
        public int CritCount { get; private set; }
        public int BackAttackCount { get; private set; }
        public int DotHitCount { get; private set; }
        public long FirstHitTicks { get; private set; }
        public long LastHitTicks { get; private set; }

        private readonly Dictionary<int, long> _damagePerTarget = new();
        private readonly Dictionary<int, (long First, long Last)> _targetTimes = new();
        private readonly Dictionary<uint, SkillStats> _skills = new();
        private readonly HashSet<int> _targets = new();

        public Accumulator(int actorId) { ActorId = actorId; }

        public void ApplyDamage(RawDamage d)
        {
            if (HitCount == 0) FirstHitTicks = d.Timestamp;
            LastHitTicks = d.Timestamp;
            TotalDamage += d.Damage;
            HitCount++;
            if (d.IsCritical) CritCount++;
            if (d.IsBackAttack) BackAttackCount++;
            _targets.Add(d.TargetId);
            _damagePerTarget[d.TargetId] = _damagePerTarget.GetValueOrDefault(d.TargetId) + d.Damage;
            if (!_targetTimes.TryGetValue(d.TargetId, out var t))
                _targetTimes[d.TargetId] = (d.Timestamp, d.Timestamp);
            else
                _targetTimes[d.TargetId] = (t.First, d.Timestamp);

            // SkillStats 는 기존 mutable 객체 — view 가 SkillStats 의 Apply 를 호출해야 하는데
            // SkillStats.Apply 는 DamageEvent 시그니처. 어댑터로 합성하거나, view 가 자체적으로
            // 재구성. Phase 2 일단 카운터만 유지 — 스킬별 통계는 UI 에서 기존 PlayerStats 가
            // 제공 (병렬 운영). Phase 3 에서 SkillStatsView 분리.
        }

        public void ApplyDot(RawDotTick dot)
        {
            if (HitCount == 0) FirstHitTicks = dot.Timestamp;
            LastHitTicks = dot.Timestamp;
            TotalDamage += dot.Damage;
            HitCount++;
            DotHitCount++;
            _targets.Add(dot.TargetId);
            _damagePerTarget[dot.TargetId] = _damagePerTarget.GetValueOrDefault(dot.TargetId) + dot.Damage;
            if (!_targetTimes.TryGetValue(dot.TargetId, out var t))
                _targetTimes[dot.TargetId] = (dot.Timestamp, dot.Timestamp);
            else
                _targetTimes[dot.TargetId] = (t.First, dot.Timestamp);
        }

        public PlayerStatsView Build() => new PlayerStatsView
        {
            ActorId = ActorId,
            TotalDamage = TotalDamage,
            HitCount = HitCount,
            CritCount = CritCount,
            BackAttackCount = BackAttackCount,
            DotHitCount = DotHitCount,
            FirstHitTicks = FirstHitTicks,
            LastHitTicks = LastHitTicks,
            DamagePerTarget = _damagePerTarget,
            TargetTimes = _targetTimes,
            Skills = _skills,
            Targets = _targets,
        };
    }
}
