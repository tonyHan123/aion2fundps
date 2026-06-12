# Event Sourcing 아키텍처 (v0.2 코어 리팩토링)

## 목적

Cold-start 시나리오 (게임 도중 미터 켜기) 에서 데이터 누수 / 잘못된 attribution / proxy timeout 문제를 **근본적으로 제거**.

기존 mutable state 모델은 "패킷이 항상 정해진 순서로 온다" 는 전제. cold-start 에선 setup 패킷이 과거에 있어 영영 못 받으므로 가정이 깨짐.

신규 모델은 raw event 를 보존하고 alias 정보가 도착하는 대로 view 를 재구성. **late binding 으로 cold-start 가 자연스럽게 해결됨**.

---

## 핵심 원칙

1. **Single source of truth = (EventLog, AliasLog)**. 두 append-only 자료구조.
2. **View 는 순수 함수의 출력**. mutate 없음. 매 tick `f(EventLog, AliasLog) → View`.
3. **Late binding = 자동**. alias 가 늦게 도착해도 다음 tick 재계산이 과거 데이터에 자동 적용.
4. **Replay 가능**. EventLog 직렬화 → 디스크 → 재현. 디버깅 / 테스트 / export 에 모두 동일 path.

---

## 컴포넌트

### `RawEventStore` (Aion2FunDps.Core/EventSourcing/)

```csharp
public sealed class RawEventStore
{
    // append-only. raw_actor_id, timestamp 그대로 보존. mutate 금지.
    private readonly List<RawDamage> _damage = new();
    private readonly List<RawHpUpdate> _hp = new();
    private readonly List<RawDotTick> _dot = new();
    private readonly List<RawSummonSpawn> _summon = new();
    private readonly List<RawCombatPower> _cp = new();
    private readonly List<RawNickname> _nick = new();
    // ... 모든 raw event 종류

    public void Record(IRawEvent ev);
    public IEnumerable<IRawEvent> Iterate(long sinceTimestamp = 0);
    public int TotalCount { get; }
    public void Reset(); // 세션 reset (보스 처치 → 다음 풀)
}
```

- `IRawEvent`: 공통 인터페이스 (`Timestamp`, `RawActorId`, etc.)
- 메모리. 한 fight 약 1만 event × 64B = ~640KB. 세션 (10 fights) ~6MB. OK.

### `AliasLog` (Aion2FunDps.Core/EventSourcing/)

```csharp
public sealed class AliasLog
{
    // (timestamp, raw_id, canonical_id, source) 시간순 누적
    private readonly List<AliasEntry> _entries = new();

    public void Record(int raw, int canonical, AliasSource source, long ts);

    // 시점 t 에서 raw 의 canonical resolve. alias 없으면 raw 자체 리턴.
    public int Resolve(int raw, long at);

    // 현재 시점 기준 resolve (UI 표시용)
    public int ResolveNow(int raw);

    public IEnumerable<AliasEntry> Iterate();
}

public enum AliasSource
{
    SelfNick, BulkInfo, PartyAssembly, OtherNick, SummonSpawn, Proxy
}
```

- alias 변경은 드뭄 (세션당 수십 ~ 수백 건). lookup 은 hash 기반 O(1) approximation 가능.
- 시점 기반 resolve. 미래 확장 (시간순 정확한 view replay) 에 사용.

### `SessionView` (Aion2FunDps.Core/EventSourcing/)

```csharp
public sealed class SessionView
{
    // canonical_id 기준 집계. mutate 가능하지만 외부에서 재할당.
    public Dictionary<int, PlayerStatsView> PlayerStats { get; init; } = new();
    public BossStateView BossState { get; init; } = new();
    public long TotalCrewDamage { get; set; }
    public DateTime BuiltAt { get; set; }
    // ...
}
```

PlayerStatsView 는 현재 `PlayerStats` 클래스 와 유사한 표면. UI 가 바인딩.

### `ViewBuilder` (Aion2FunDps.Core/EventSourcing/)

```csharp
public static class ViewBuilder
{
    public static SessionView Build(RawEventStore events, AliasLog aliases, BossTracker boss)
    {
        var view = new SessionView();
        foreach (var ev in events.Iterate())
        {
            int canonical = aliases.Resolve(ev.RawActorId, ev.Timestamp);
            view.GetOrCreate(canonical).Apply(ev);
        }
        view.BossState = BuildBossState(boss);
        view.BuiltAt = DateTime.UtcNow;
        return view;
    }
}
```

순수 함수. 입력 같으면 출력 같음. 테스트 / 재현 쉬움.

### `IncrementalViewCache` (Aion2FunDps.Core/EventSourcing/, Phase 5)

매 tick 풀 빌드는 O(n) — 큰 세션엔 부담. 대신.
- 새 event 만 적용 (대부분 케이스 O(Δ))
- alias 변경 시 그 raw_id 의 모든 event 만 재귀속 (O(events_with_that_raw_id))

```csharp
public sealed class IncrementalViewCache
{
    private SessionView _view = new();
    private int _lastAppliedEventIndex = 0;
    private long _lastAliasLogVersion = 0;

    public SessionView Refresh(RawEventStore events, AliasLog aliases);
    private void ApplyNewEvents(...);
    private void ReattributeOnAliasChange(...);
}
```

---

## 데이터 흐름

```
Capture → Dispatcher → RawEvent 생성
                            ↓
                       RawEventStore.Record()
                            ↓
                       (또는 alias 정보면)
                       AliasLog.Record()
                            ↓
                       (UI tick 500ms)
                            ↓
                       IncrementalViewCache.Refresh()
                            ↓
                       SessionView → MainViewModel 바인딩
```

---

## Cold-start 가 해결되는 과정

시나리오. 미터 켤 때 self 의 dungeon_id 가 8204, 실제 lobby canonical 은 46569. SELF_NICK 못 받음.

**기존 모델**.
- damage event raw=8204 도착 → PlayerStats[8204] 에 누적
- 나중에 SELF_NICK 도 안 옴 → 8204 → 46569 alias 영영 안 생김
- PlayerStats[8204] 는 orphan. UI 안 보임 또는 "Actor_8204"

**신규 모델**.
- damage event raw=8204 → RawEventStore 에 그대로 보존
- 매 tick ViewBuilder: AliasLog.Resolve(8204) → alias 없음 → canonical=8204 → SessionView.PlayerStats[8204] 에 집계
- 시간 경과 → SUMMON_SPAWN 도착, OwnerName 매칭 → AliasLog.Record(8204, 46569, SummonSpawn)
- 다음 tick ViewBuilder: AliasLog.Resolve(8204) → 46569 → SessionView.PlayerStats[46569] 에 집계
- **자동으로 과거 데미지 5M 이 46569 로 옮겨감**. 코드 분기 없음.
- UI 에서 자연스럽게 placeholder → 진짜 닉으로 swap.

이게 late binding 의 본질.

---

## 비교

| 항목 | 기존 (mutable) | 신규 (event sourcing) |
|---|---|---|
| state mutation | 곳곳 흩어짐 | append-only 만 |
| late alias 처리 | MergeFrom 등 수동 분기 | 자동 (view 재계산) |
| 일관성 | 분기 누락 시 깨짐 | 입력만 정확하면 보장 |
| 메모리 | 작음 (state만) | 크지만 수용 가능 (수십 MB) |
| CPU | event 처리만 | event + view 재계산 |
| 테스트 | mock 어려움 | canned event → 검증 쉬움 |
| 디버깅 | "어디서 mutate 됐지" 추적 | event log 검색만 |
| Replay | 불가 | 가능 |
| Export | 별도 코드 | view snapshot 직렬화 |

---

## 위험 / 완화

1. **메모리 폭증**
   - 한 세션 (1 hour) 수십 MB 예상. 수용.
   - 세션 reset 시 EventStore 도 reset.
   - 위험: 누군가 미터 24시간 켜놓음. 안전판: ring buffer / size cap.

2. **CPU 비용**
   - 매 tick 풀 빌드 O(n). 1만 event → ~5ms.
   - Phase 5 incremental cache 로 O(Δ). 대부분 tick <1ms.

3. **회귀 리스크**
   - 기존 mutable state 와 신규 view 가 매 tick 같은 결과인지 검증 단계 (Phase 2).
   - 불일치 발견 시 양쪽 비교 로깅.

4. **시간 비용**
   - 솔로 dev 2-3주 집중.
   - 그 사이 새 기능 / 버그 픽스 stop (긴급 hotfix 제외).

---

## 비고: NCSoft API 활용

별도 옵션 (B-c). 본인 프로필만 외부 API 로 가져옴. 이 spec 과 직교. event sourcing 본체 끝나고 검토.

---

## 다음 단계

[Checklist](event-sourcing-checklist.md) 참고. Phase 1 부터 점진 진행.
