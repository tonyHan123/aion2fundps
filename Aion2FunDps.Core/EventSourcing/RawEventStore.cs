// Append-only 이벤트 저장소. 한 번 들어간 IRawEvent 는 mutate 되지 않음.
namespace Aion2FunDps.Core.EventSourcing;

/// <summary>
/// 모든 raw 이벤트의 append-only 저장소. event sourcing 의 single source of truth.
///
/// 사용 패턴.
///   - capture thread: Record(ev) 로 append. mutate 절대 X.
///   - UI tick: Iterate() 로 ViewBuilder 가 풀 스캔하며 view 재구성.
///   - 보스 처치 직후 다음 풀 시작: Reset() 으로 깨끗하게 비움 (auto-reset 모드).
///
/// Phase 1 에선 단일 List 로 시작. 타입별 분리 / ring buffer / size cap 은 메모리
/// 측정 후 필요해지면 도입. 한 fight 약 1만 event × ~80B = 800KB 수준.
///
/// 동시성. DpsAggregator._stateLock 안에서만 호출 — 내부 lock 없음.
/// </summary>
public sealed class RawEventStore
{
    private readonly List<IRawEvent> _events = new();

    public int TotalCount => _events.Count;

    /// <summary>이벤트 1개 append. 호출 순서 = 도착 순서. timestamp 순서가 아닐 수 있음
    /// (드물지만 capture / dispatcher 큐잉으로 약간의 역순 발생 가능). ViewBuilder 는
    /// timestamp 의존이지 인덱스 의존이 아니어야 함.</summary>
    public void Record(IRawEvent ev)
    {
        _events.Add(ev);
    }

    /// <summary>전체 iterate (도착 순). ViewBuilder 풀 빌드용.</summary>
    public IReadOnlyList<IRawEvent> All => _events;

    /// <summary>since timestamp 이후 이벤트만. incremental view cache (Phase 5) 용.</summary>
    public IEnumerable<IRawEvent> Since(long sinceTimestamp)
    {
        foreach (var ev in _events)
            if (ev.Timestamp >= sinceTimestamp) yield return ev;
    }

    /// <summary>전체 비움. 보스 처치 → 다음 풀 ResetCore 와 동기화.</summary>
    public void Reset()
    {
        _events.Clear();
    }
}
