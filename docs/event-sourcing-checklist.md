# Event Sourcing 리팩토링 Checklist

각 phase 안의 task 를 차례로 체크. 누락 없이.

---

## Phase 0. Spec + 인터페이스 정의

- [x] 아키텍처 spec 문서 (event-sourcing-spec.md)
- [x] checklist 문서 (이 파일)
- [x] context notes 문서 (event-sourcing-context.md)
- [ ] `IRawEvent` 인터페이스 정의 (Aion2FunDps.Core/EventSourcing/IRawEvent.cs)
- [ ] `RawDamage`, `RawHpUpdate`, `RawDotTick` 등 record type 정의
- [ ] `AliasSource` enum 정의
- [ ] 기존 모델과의 매핑 표 작성 (어느 기존 클래스 → 어느 신규 컴포넌트)

## Phase 1. RawEventStore + AliasLog 추가 (기존 코드 옆에)

- [x] `RawEventStore.cs` 구현 — append + iterate + reset
- [x] `AliasLog.cs` 구현 — record + resolve (시점 기반)
- [x] `DpsAggregator` 에 `_rawEventStore` / `_aliasLog` 필드 추가
- [x] DamageEvent 처리 시 RawEventStore 에 추가 (기존 PlayerStats mutate 와 병행)
- [x] OTHER_NICK / SELF_NICK / BULK_INFO 등에서 AliasLog 도 기록
- [x] 빌드 통과 + 기존 UI 동작 동일 (RawStore 는 아직 안 읽음, shadow 만)
- [ ] 메모리 사용량 측정 (1 hour 세션 기준) — 실측 테스트 필요

## Phase 2. ViewBuilder + UI 전환 (병렬 검증)

- [x] `SessionView`, `PlayerStatsView`, `BossStateView` 정의
- [x] `ViewBuilder.Build()` 구현 — 순수 함수
- [x] DpsAggregator 에 매 tick view 빌드 추가 (MainViewModel.Refresh 에서 BuildView 호출)
- [x] 빌드된 view 와 기존 mutable state 비교 (DEBUG 빌드 한정 view-verify.log)
- [ ] 불일치 발생 시 어떤 path 가 다른지 식별 — 사용자 게임 테스트 필요
- [ ] 모든 fight 시나리오 (정상, cold-start, phase transition, retry, multi-boss) 에서 일치 확인
- [ ] UI 가 view 를 읽도록 전환 (DataContext) — 검증 통과 후
- [ ] 양쪽 (기존 + view) 동시 운영 유지 — 현재 그렇게 됨 (UI 는 기존 PlayerStats 사용)

## Phase 3. Late binding 활성화

- [ ] 늦게 도착하는 alias 처리 케이스 작성
  - [ ] SUMMON_SPAWN 으로 dungeon_id → canonical alias
  - [ ] OTHER_NICK 으로 dungeon_id → canonical alias
  - [ ] PARTY_ASSEMBLY post-kill 으로 alias 일괄 도착
- [ ] view 재계산이 자동으로 과거 데미지를 canonical 에 옮기는지 검증
- [ ] cold-start 시나리오 유닛 테스트 (canned event 시퀀스)
- [ ] proxy 메커니즘 단순화 검토 (event sourcing 이 같은 일을 함)

## Phase 4. 기존 mutable state 제거

- [ ] PlayerStats, Current.Session 등 legacy 코드 제거
- [ ] DpsAggregator 의 mutate 로직 제거
- [ ] view 만 진실원으로 운영
- [ ] 회귀 테스트 (Phase 2 의 시나리오 다시)

## Phase 5. 성능 최적화 (incremental cache)

- [ ] `IncrementalViewCache.cs` 구현
- [ ] 새 event 만 적용하는 path
- [ ] alias 변경 시 해당 raw 의 모든 event 재귀속 path
- [ ] tick 비용 측정 (UI Refresh < 5ms 목표)
- [ ] 메모리 / CPU 프로파일링

## Phase 6. 유닛 테스트 + 문서화

- [ ] canned event 시퀀스 → assert view state 패턴 구축
- [ ] cold-start scenario 테스트
- [ ] phase transition (same mob_code) 테스트
- [ ] dungeon retry (different entity, same mob_code series) 테스트
- [ ] multi-boss room (4 bosses simultaneously) 테스트
- [ ] PvP detection 회귀 테스트 (필드 random player 제외)
- [ ] CHANGELOG 작성
- [ ] README 업데이트 (event sourcing 차별점 안내)

---

## 진행 중 발생 시 추가할 작업

여기에 새 task 추가. 누락 방지.

- [ ] (예: "AliasLog 의 Resolve 가 O(n) 이라 느림 — bucket index 추가 필요")
