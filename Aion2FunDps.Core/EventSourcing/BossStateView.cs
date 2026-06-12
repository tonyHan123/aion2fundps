// 보스 / 인카운터 상태 snapshot. ViewBuilder 가 BossTracker 에서 직접 흡수.
namespace Aion2FunDps.Core.EventSourcing;

/// <summary>
/// SessionView 안의 boss / encounter 상태. spec 결정대로 BossTracker 의 mutable 상태를
/// 그대로 흡수하지 raw event 로 재구성하지 않음 (Phase 2 일단 BossTracker 가 진실원).
/// Phase 5 incremental 단계에서 RawHpUpdate 기반 재구성 검토 가능.
/// </summary>
public sealed class BossStateView
{
    public int? FocusedEntityId { get; init; }
    public bool IsBossMode { get; init; }
    public long FocusedCurrentHp { get; init; }
    public long FocusedMaxHp { get; init; }
    public int? FocusedMobCode { get; init; }
    public int? LastKilledBossId { get; init; }
    public long? FrozenTotalPartyDamage { get; init; }
}
