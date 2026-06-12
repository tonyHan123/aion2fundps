// 매 tick ViewBuilder 가 만들어내는 read-only session snapshot. UI 가 바인딩 / 읽기 전용.
namespace Aion2FunDps.Core.EventSourcing;

/// <summary>
/// 한 시점의 session snapshot. ViewBuilder.Build 의 단일 출력.
///
/// 불변성. SessionView 의 모든 필드는 init-only / IReadOnly. 매 tick 새로 만들어짐.
/// 일관성. PlayerStats / BossState / TotalCrewDamage 가 같은 빌드 시점의 원자적 snapshot.
/// </summary>
public sealed class SessionView
{
    /// <summary>canonical_id → PlayerStatsView. raw_id 는 AliasLog 가 이미 fold 했음.</summary>
    public IReadOnlyDictionary<int, PlayerStatsView> PlayerStats { get; init; }
        = new Dictionary<int, PlayerStatsView>();

    public BossStateView BossState { get; init; } = new BossStateView();

    /// <summary>전체 player 데미지 합 (필터 안 함). party-only 합산은 소비자가 _partyMembers
    /// 와 교차해서 계산. ViewBuilder 는 멤버십 모름.</summary>
    public long TotalDamage { get; init; }

    public int TotalEventsApplied { get; init; }
    public DateTime BuiltAt { get; init; }
}
