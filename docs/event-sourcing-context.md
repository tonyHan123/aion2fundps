# Event Sourcing 리팩토링 — Context Notes

작업 진행하며 내린 결정 / 발견 / 변경 사항. 차후 (다른 세션 / 다른 사람) 에서 "왜 이렇게 했지?" 가 안 되도록 누적 기록.

---

## 2026-05-28. Phase 0 시작.

### 결정 1. 왜 Event Sourcing 인가.

기존 mutable state 모델은 "패킷 도착 순서" 에 강하게 의존. cold-start (게임 도중 미터 켜기) 처럼 setup 패킷이 과거에 있어 못 받으면 데이터 잃음.

대안 평가.
- **Persistent local cache** — 도움 됨 (B). 같이 던전 자주 도는 사람 인식 가능. 근데 처음 만나는 사람 / 본인 self id 결정 못함.
- **NCSoft API** — self 한정 (C). 다른 사람 정보는 약관/PIPA 위험.
- **Event Sourcing** — late binding 으로 시간 무관 정확성 (A). 근본적.

A 만이 cold-start 의 진짜 해결. B / C 는 보조.

### 결정 2. append-only EventLog + AliasLog.

mutate 절대 X. 한 번 들어간 이벤트 / alias 는 그대로. 변경은 새 레코드 추가로만.

이유.
- 일관성 유지 쉬움
- replay / debugging 가능
- 매 tick view 재계산이 단순 함수

### 결정 3. View 는 매 tick 빌드.

처음엔 풀 빌드 (O(n)). Phase 5 에서 incremental cache 로 O(Δ).

이유.
- Phase 1-3 동안 빌드 비용 무시 가능 (1만 event ~5ms)
- Incremental 은 복잡함. 정확성 먼저, 성능 나중.

### 결정 4. 마이그레이션 = 점진적 (빅뱅 X).

Phase 2 에서 기존 mutable state 와 신규 view 가 **동시 동작**, **결과 비교** 로 회귀 잡음. 안정성 확인 후 Phase 4 에서 legacy 제거.

이유.
- 솔로 dev 인 사용자가 한 번에 다 갈아엎으면 회귀 잡기 어려움
- 병렬 검증 단계가 안전판

---

## 기존 모델 → 신규 매핑

(작업 진행하며 정확히 채울 것)

| 기존 | 신규 |
|---|---|
| `PlayerStats[actor_id]` (mutate) | `SessionView.PlayerStats[canonical]` (rebuild) |
| `NicknameRegistry._aliases` | `AliasLog` |
| `NicknameRegistry._entries` | `NicknameLookup` (alias log 의 nickname 측면) |
| `Current.Session` | `SessionView` |
| `DpsAggregator.OnEvent(...) mutate` | `RawEventStore.Record(...) + AliasLog.Record(...)` |
| `MergeFrom` (orphan merge) | 자동 (alias 도착 시 view rebuild 가 처리) |
| `_proxySelfActorId` | 단순화 가능 (event sourcing 이 자동 처리) |

---

## 우려 사항 / 미해결

- 매 tick 풀 빌드 비용. Phase 5 까지 측정해보고 결정.
- BossTracker 는 기존 상태 유지. ViewBuilder 가 BossTracker 의 snapshot 만 view 에 흡수.
- 메모리. ring buffer 필요한지 검토 (긴 세션).

---

## 발견 (작업 중 추가)

### 2026-05-28. Phase 1 구현 결정.

**결정 5. AliasLog.Resolve 는 "모든 alias" 기준, "시점 이전 alias" 아님.**

처음엔 spec 의 `Resolve(raw, at)` 를 "at 이전에 도착한 alias 만" 으로 해석. 근데 그러면 cold-start 가 안 풀림.

시나리오. damage event (t=100, raw=8204) → AliasLog 에 t=150 에서 alias(8204→46569) 도착.
- "at 이전 alias 만" 정책 → Resolve(8204, t=100) → 46569 alias 가 미래 → 무시 → canonical=8204. 과거 데미지 영영 8204 행에 머무름.
- "모든 alias 채택" 정책 → Resolve(8204, t=100) → 46569 적용 → 다음 ViewBuilder 풀빌드 시 46569 에 fold. **자동 late binding.**

후자가 본질. timestamp 는 정확한 attribution 의 "정답" 을 정함 (alias 가 t=150 에 등장했어도 "이 raw 는 사실 46569 였다" 는 사실은 t=100 에도 참).

Phase 5 incremental cache 에서 alias 새로 도착 시 "그 raw 의 모든 과거 event 재귀속" 로직과 일치.

**결정 6. AliasLog 는 ResetCore 에서 보존, RawEventStore 는 비움.**

- RawEventStore: 보스별 풀 reset (auto-reset 모드). 메모리 bounded.
- AliasLog: 세션 누적. NicknameRegistry 가 alias 를 보존하는 것과 동일 정책 — alias 지식은 풀 reset 과 무관하게 가치 있음 (다음 풀에서 같은 dungeon_id 가 또 등장해도 즉시 해소).

**결정 7. Boss callback 의 RawBossKill/Engaged 는 UtcNow.Ticks 사용.**

BossTracker 의 callback signature 가 (int bossId) 라 timestamp 없음. 이 부분은 단발성 marker 라 정확도 손실이 무시할 만함. Phase 2 ViewBuilder 가 boss state 를 BossTracker snapshot 으로 직접 받으니 이 raw event 들은 디버깅/감사용에 가까움.

**결정 8. CombatBoundary / DungeonAnnouncement / PartyLeft / EncounterAnnouncement 는 shadow 미기록.**

view 빌드에 직접 입력이 안 됨 (meta 이벤트). 필요해지면 IRawEvent 구현체 추가하는 식으로 점진적. 지금 미리 만들면 dead weight.

### 2026-05-28. Phase 2 구현 결정.

**결정 9. PlayerStatsView 는 packet timestamp 기준 fight duration, wall-clock 안 씀.**

기존 PlayerStats.Dps 는 `DateTime.UtcNow - _firstHitAt`. wall-clock 의존이라 view 가 같은 시점에 두 번 빌드되면 결과가 달라짐 (= 순수 함수 아님). View 는 `(LastHitTicks - FirstHitTicks)` 같은 packet 시간 간격을 expose, UI 가 표시 시 변환.

다만 "지금 막 친 후 t 초 동안 안 치고 있음" 같은 wall-clock decay 는 view 자체에 없음. UI 가 BuiltAt - lastHitTicks 변환으로 처리하거나, frozen-dps 같은 명시적 신호 도입. Phase 3 에서 다듬음.

**결정 10. SkillStats 는 view 가 아직 재구성 안 함.**

기존 SkillStats 객체는 mutable + DamageEvent 시그니처에 맞춰 Apply 함. RawDamage 로부터 재구성하려면 SkillStatsView 어댑터 필요. Phase 2 첫 컷에선 빈 dict 로 둠 — UI 가 스킬별 통계 표시할 때는 기존 PlayerStats 를 사용 (병렬 운영 정책 그대로).

**결정 11. BossStateView 의 FocusedMobCode / LastKilledBossId / FrozenTotalPartyDamage 는 일단 비움.**

ViewBuilder 는 BossTracker 만 받음 — DpsAggregator 의 LastKilledBossId / FrozenTotalPartyDamage 는 view 입력에 없음. Phase 2 다음 단계에서 ViewBuilder 시그니처에 BossSnapshot 추가하거나, SessionView 에 wrap.

**결정 12. 검증 로그는 1초마다 한 줄, view-verify.log.**

매 tick (500ms) 마다 쓰면 로그 폭증. 1초 throttle. 비교는 단순 TotalDamage 합 + 행 수. 일치하면 MATCH, 다르면 DIFF — DIFF 시 사용자가 게임 시점 / 시나리오 메모해서 추적.

검증 로그가 자주 DIFF 면 그 시점의 raw-event-vs-mutable-Apply 경로 차이가 있다는 뜻 — Phase 3 의 alias 정밀화 / 누락 이벤트 식별의 시작점.
