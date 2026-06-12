// raw_id → canonical_id 매핑의 append-only 로그. 시점 기반 resolve 지원.
namespace Aion2FunDps.Core.EventSourcing;

/// <summary>AliasLog 엔트리. (시각, raw, canonical, 출처) 한 줄.</summary>
public readonly record struct AliasEntry(
    long Timestamp,
    int RawId,
    int CanonicalId,
    AliasSource Source
);

/// <summary>
/// raw_actor_id → canonical_id 매핑의 append-only 로그.
///
/// Phase 1 의 resolve 정책 = "시점 이전의 가장 최신 alias 채택" (단순).
/// Phase 3 에서 source priority (SelfNick &gt; BulkInfo &gt; ... &gt; Proxy) 도입 검토.
///
/// 시점 기반 resolve 가 핵심.
///   - damage event (t=100, raw=8204) 가 RawEventStore 에 들어감
///   - 나중에 t=150 에서 SUMMON_SPAWN 으로 alias(8204 → 46569) 가 기록됨
///   - 다음 ViewBuilder 풀빌드: Resolve(8204, t=100) → canonical=8204 일까 46569 일까?
///   - 답. **46569** — alias 가 미래에 발견되더라도 과거 event 는 그 canonical 로 fold.
///     이것이 late binding 의 본질. 시점은 alias 도착 순서가 아니라 attribution 의 "정답"
///     을 정함. (구버전에서 Resolve(raw, ts) 가 ts 이전 alias 만 보면 cold-start 가 안
///     풀림.) 따라서 현재 구현은 raw 별 *모든* alias 중 가장 권위적인 것을 반환.
///
/// 동시성. DpsAggregator._stateLock 안에서만 호출 — 내부 lock 없음.
/// </summary>
public sealed class AliasLog
{
    private readonly List<AliasEntry> _entries = new();
    // raw → 그 raw 의 모든 alias entry. resolve 빠르게.
    private readonly Dictionary<int, List<AliasEntry>> _byRaw = new();

    public int TotalCount => _entries.Count;
    public IReadOnlyList<AliasEntry> All => _entries;

    /// <summary>alias 1개 기록. 같은 (raw, canonical) 이 여러 번 기록될 수 있음 — 무시하지
    /// 않고 다 보존. resolve 가 source priority / 최신성 으로 정리.</summary>
    public void Record(int raw, int canonical, AliasSource source, long timestamp)
    {
        var entry = new AliasEntry(timestamp, raw, canonical, source);
        _entries.Add(entry);
        if (!_byRaw.TryGetValue(raw, out var list))
        {
            list = new List<AliasEntry>(2);
            _byRaw[raw] = list;
        }
        list.Add(entry);
    }

    /// <summary>raw 의 canonical resolve. alias 없으면 raw 그대로 리턴.
    ///
    /// Phase 1 정책. raw 의 모든 alias 중 가장 신뢰도 높은 source 의 가장 최근 timestamp 채택.
    /// at 인자는 현재 시점 무시 (future alias 도 적용 — 위 클래스 docstring 참고).</summary>
    public int Resolve(int raw, long at = long.MaxValue)
    {
        if (!_byRaw.TryGetValue(raw, out var list) || list.Count == 0)
            return raw;

        // SelfNick=0 < BulkInfo=1 < ... < Proxy=5 — 작을수록 신뢰. enum 순서대로.
        AliasEntry best = list[0];
        for (int i = 1; i < list.Count; i++)
        {
            var cand = list[i];
            // source priority 우선, 동률이면 timestamp 최신.
            if ((int)cand.Source < (int)best.Source) best = cand;
            else if (cand.Source == best.Source && cand.Timestamp > best.Timestamp) best = cand;
        }
        return best.CanonicalId;
    }

    /// <summary>현 시점 기준 (모든 alias 적용). UI 표시용 alias.</summary>
    public int ResolveNow(int raw) => Resolve(raw, long.MaxValue);

    public void Reset()
    {
        _entries.Clear();
        _byRaw.Clear();
    }
}
