// Event Sourcing 리팩토링의 핵심 인터페이스 — append-only RawEventStore 에 들어가는
// 모든 패킷-유래 이벤트의 공통 표면.
namespace Aion2FunDps.Core.EventSourcing;

/// <summary>
/// RawEventStore 에 누적되는 append-only 이벤트의 공통 인터페이스.
///
/// 핵심 불변성.
///   - mutate 금지. 한 번 store 에 들어가면 그대로 보존.
///   - raw_actor_id 그대로. canonical 로 resolve 는 ViewBuilder 단계에서 alias log 와 함께.
///   - timestamp 필수. 시점 기반 alias resolve / 재현 / 디버깅의 기준.
///
/// 이 인터페이스를 구현하는 record 는 모두 sealed + readonly. 새 이벤트 종류 추가 시
/// (1) 이 인터페이스 구현 (2) RawEventStore 에 list 추가 (3) ViewBuilder 의 Apply
/// switch 에 분기 추가 — 세 군데만 손대면 됨.
/// </summary>
public interface IRawEvent
{
    /// <summary>패킷 타임스탬프 (pcap ticks). 시점 기반 alias resolve 의 기준.</summary>
    long Timestamp { get; }

    /// <summary>이 이벤트의 주체 actor id (raw, alias 적용 전).
    /// damage 의 경우 공격자 id, hp_update 의 경우 대상 entity id 등. 이벤트 종류 별 의미.</summary>
    int RawActorId { get; }
}
