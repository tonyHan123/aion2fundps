# 2026-06-13 성역 테스트 종합 분석안

세션 23:46:23 시작(06-12) / MARK #1 = 02:40:48(06-13) / 빌드 = 앰비언트 글래스 리디자인 WIP(700c965).
성역 = 4인 파티 2개 합산 8인 레이드. 파티 밖 인원이 같은 보스를 치는 오픈 구조.

---

## 증상 1. 전투력 표시 안 됨

### 판정 — 데이터 정상, 표시 경로 부재 (리디자인 의도였으나 실전에서 실패)

| 근거 | 위치 |
|---|---|
| 02:34:46 덤프에서 파티 8명 전원 cp 보유 (540k~631k) | identity-debug.log |
| 리셋(RESET_CORE)은 registry 비파괴, CP는 단조 증가만 | reset-debug.log + `NicknameRegistry.UpdateCombatPowerByName` |
| MARK 시점 crew=8 rows=8 정상 | share-debug.log 02:40:48 |
| CP가 보이는 곳 = 행 호버 툴팁뿐 (`MainWindow.xaml:306`) | 06-12 리디자인 "전투력 → 행 툴팁으로 이동" |
| `vm.CombatPowerDisplay`(`MainViewModel.cs:583`)는 세팅만 되고 바인딩하는 XAML 0개 | 죽은 프로퍼티 |

전투 중 호버 없이는 CP 확인 불가 → 사용자 입장에선 "표시 안 됨"과 동일.

### 수정안
- **1-A (권장)** 행에 CP 재노출. 닉네임 아래/옆 보조 텍스트로 작게 (`CombatPowerBrush` 테마 리소스 3종 이미 존재). `CombatPowerDisplay` 바인딩만 추가하면 됨 — 소규모.
- **1-B** 툴팁 유지 + 컴팩트 토글 옵션으로 행 CP on/off — 설정 항목 추가 필요, 중규모.
- 어느 쪽이든 미사용 확정 시 `CombatPowerDisplay` 정리 여부 결정.

---

## 증상 2. 일부 파티원 DPS 0 표시 ("매크로 하위 사람들")

### [확정 2026-06-13 추가 분석] 원인 + 매핑 소스 패킷

- job=30 = **치유성**(`JobClass.cs:71`, 29~32). 뇌성·옹팡 둘 다 치유성.
- 둘은 딜 0이 아님 — op=4136 패킷에서 **뇌성↔raw 12218, 옹팡↔raw 5376** 동반.
  그 raw id는 02:34 덤프에서 각각 dmgTot 617,577 / 2,839,464 의 익명 Cleric.
  즉 실제로 61만·283만 딜을 했으나 닉 미해소로 "치유성N" 익명 행에 적체됨.
- **op=4136** = 던전 내 닉+entity 정보를 담은 미처리 패킷.
  - op1=0x36 이라 PartyMemberStatusHandler(0x97/0xe2 only, :67)가 안 잡음.
  - 닉 = offset 9~ (nameLen@8). raw id = 패킷 뒤쪽(닉 6byte 케이스 @135).
  - 이미 0297 로비로 해소된 멤버(한보름·바키s)는 raw 동반이 안 잡힘 → op=4136이
    "미해소 던전 raw ↔ 닉"을 담는다는 정황 강화.
  - A2Viewer는 op=4136 미사용. (serverId,nickname) memberHint 캐싱으로 우회
    (`PartyStreamParser.RememberMemberHint`).
- **남은 관문(미확정)** — op=4136은 multi-record. 닉과 raw가 같은 record로 묶이는지
  (직접 매핑) vs 단지 같은 시야 패킷에 동반인지 확정 필요. 성역은 파티 밖 인원이
  많아 오귀속 시 남의 딜이 붙음. record 경계/entity_id offset reverse가 핸들러 전제.

### [구현 완료 2026-06-13] op=4136 핸들러

- reverse 확정 (tools/reverse_4136.py, raw_context_4136.py):
  - record1: nameLen@8, nick@9 (닉 길이 무관 — 한보름 9byte 검증)
  - entity_id 시그니처: `07 02 06 [id 4 LE] 3d 0a 00 00` (뒤 마커 65/65 고정)
  - 닉record.eid == raw record.eid, raw-in-other-eid = **0** (오귀속 없음)
- 구현:
  - `Aion2FunDps.Protocol/Handlers/CombatEntityRemapHandler.cs` (신규) — op=4136 파싱,
    NicknameInfo(entityId→nick) emit. server/job/cp=0 (registry no-downgrade가 보존).
  - `PacketDispatcher.cs` — op=0x41 0x36 분기 추가 (OTHER_NICK 다음). ENTITY_REMAP 로깅.
  - 잡 매칭(TryJobMatchAdoption)은 **건드리지 않음** — op=4136이 닉 해소하면 그 raw가
    "닉 미상 + 미배정" 대상에서 자동 제외되어 잡 매칭이 자연히 fallback 강등됨.
    op=4136이 안 오는 상황의 안전망으로 유지.
- 검증 (tools/verify_remap, frames-dump 전체 재생):
  - op=4136 7363개 중 278개 매핑, **ambiguous(1 entityId↔2+ nick) = 0**.
  - 옹팡↔5376, 뇌성↔12218 기대대로. 한 닉이 여러 entityId = entityId 재발급(부활/룸전환),
    닉 병합으로 수렴 → 정상.
  - 전체 솔루션 빌드 오류 0.
- 동작: NicknameRegistry.Register(닉 기존 canonical에 alias) + RegisterCanonical(orphan
  데미지 MergeFrom) → 던전 raw에 쌓인 61만·283만 딜이 뇌성·옹팡 행에 자동 합산.
- 미검증(다음 실측 필요): 실제 게임에서 던전 중간 입장 시 op=4136이 충분히 자주 와서
  모든 멤버를 즉시 해소하는지. frames-dump엔 풍부했으나 라이브 재확인 권장.

### 판정(초기) — 같은 직업 2+명이면 닉↔actor 페어링이 구조적으로 불가능한 상태

근본 배경 — 게임 업데이트로 던전 인스턴스 내 actor↔닉 매핑 패킷 두절
(06-12 19:15 세션에서 확인. 보스딜 87~91% unresolved, 파티원 3명 alias 0·딜 0).
06-12 긴급 복구로 잡 매칭 페어링(`DpsAggregator.TryJobMatchAdoption`, :716) 투입.

긴급 복구의 한계가 이번 증상.

- 같은 클래스 미배정 멤버 2+ → 그 클래스 전체 스킵 (`DpsAggregator.cs:777`, 옆파티 오귀속 방지용 의도적 보수 설계).
- 02:34:46 덤프 실증 — **뇌성·옹팡 둘 다 job=30** → dmgTot=0, hits=0, aliases=(none).
  실제 딜은 익명 행에 적체 (raw=5376 치유성 추정 283만, raw=12218 61만 등).
- 성역에서 악화되는 이유 — 8인이라 직업 중복 확률 ↑, 파티 외 인원 딜이 보스딜의
  10~22%라 오귀속 리스크도 ↑ (02:34 3킬 커버리지 90.2% / 78.2% / 100%).

### 수정안
- **2-A (근본, 권장)** 새 actor↔닉 매핑 opcode 리버싱.
  - 재료 — frames-dump.log 168MB (이번 런 전체 프레임), identity 덤프의 미해소 raw id
    (5376, 12218, 23143, 23975, 31755, 33006, 33658, 40956, 43585)와
    뇌성·옹팡 닉네임 UTF-8 바이트를 교차 검색해 매핑 패킷 후보 식별.
  - 성공 시 잡 매칭은 fallback 강등 (코드 주석에 이미 명시된 방향).
  - 작업량 대 — 리버싱 불확실성 있음. 단 데이터는 충분.
- **2-B (임시 완화)** 동일 클래스 N명 ↔ 후보 N명일 때 보스딜 순위로 페어링.
  - 리스크 — 옆파티 같은 직업이 후보에 섞이면 남의 딜 오귀속. `JOB_MATCH_ADOPT`
    로그로 사후 검증 가능하나 실시간 표시는 틀릴 수 있음.
  - 완화책 — "후보 수 = 미배정 수 정확 일치 + 후보 간 딜 격차 임계치" 같은 게이트.
- 2-A·2-B는 병행 가능 (2-B 먼저 내보내고 2-A 완성 시 강등).

---

## 부수 발견 (이번 런 로그에서)

1. **LIVESTATUS_PHANTOM 오탐 의심** — 02:44:07 룸 체인지(994776→999978) 직후
   canonical 195014/105120/**10165(본인)** 이 5.18초 만에 phantom 판정.
   본인이 phantom 되는 건 의도 확인 필요. 멤버십 wipe로 이어졌다면 룸 전환 중
   행 깜빡임/유실 가능성. (roster-debug.log)
2. **보스 HP 풀회복** — 02:43:15 mobId=33151 이 6.17억 → 7.2억(max) 복귀.
   전멸/페이즈 리셋으로 추정. 미터기 리셋 정책(OtherAliveBoss skip)이 이 케이스를
   올바르게 다뤘는지 별도 확인 가치. (reset-debug.log)
3. **진단 로그 용량** — frames-dump.log 168MB, mobspawn-debug.log 37MB.
   레이드 1런 기준이므로 상한/로테이션 정책 필요. 단 2-A 리버싱이 끝날 때까진
   frames-dump 가 핵심 재료라 유지 권장.
4. **정상 확인 지표** — share% 합 100% 유지, view vs mutable diff=0 일관,
   다중 보스 리셋 판정 의도대로 동작. 리디자인 후 share 파이프라인 회귀 없음.

---

## 권장 진행 순서

- [x] (소) 1-A — 행에 전투력 재노출 완료 (2026-06-13). 닉네임 유동 컬럼 우측 끝에
      `CombatPowerDisplay` 골드 보조 텍스트 (NumFont 9.5, CombatPowerBrush). 빌드 오류 0.
      → 잔여 검증: 성역/일반 던전 실화면에서 행 CP 표시 확인
- [ ] (중) 2-B — 동일 클래스 N:N 딜순위 페어링 + 안전 게이트 → 검증: JOB_MATCH_ADOPT 로그로 오귀속률 확인
- [x] (대) 2-A — op=4136 핸들러 구현·검증 완료 (CombatEntityRemapHandler). 잡 매칭은 자연 강등
- [ ] (소) 부수 1 — 룸 체인지 시 본인 phantom 판정 경로 검토
- [ ] (소) 부수 3 — 진단 로그 상한/로테이션 (2-A 완료 후)

미정 사항 — "매크로 하위"가 모집 매크로로 합류한 반대편 4인 파티를 뜻하는지 사용자 확인 대기.
로그상 DPS 0 의 실제 기준은 파티 위치가 아니라 직업 중복 여부.

---

## ▶ 다음 세션 이어가기 (2026-06-13 밤 중단 지점)

**완료된 것** — op=4136 핸들러 구현·검증까지 끝. 코드는 working tree 에만 있고 **아직 커밋 안 함**.
- 변경: `Aion2FunDps.Protocol/Handlers/CombatEntityRemapHandler.cs`(신규),
  `Aion2FunDps.Protocol/PacketDispatcher.cs`(op=0x41 0x36 분기 추가, OTHER_NICK 다음).
- 검증 도구 (tools/, gitignore 라 로컬만): reverse_4136.py, raw_context_4136.py,
  find_nick_remap.py, parse_4136.py, verify_remap/ (C# end-to-end).
- 전체 빌드 오류 0. 캡처 재생 검증 통과 (278 매핑, 오귀속 0).

**바로 할 일 (우선순위 순)**
1. [x] 커밋 완료 (2026-06-13) — op=4136 핸들러 + 이 문서, 1-A 는 별도 커밋.
2. [ ] 라이브 테스트용 진단 exe 빌드 — `build-debug.ps1` (C++ 엔진까지, 수 분 소요).
       출력: `bin\debug-diagnostic\Aion2FunDps.App.exe`. 사용자가 다음 성역에서 재테스트.
       확인 포인트: 던전 중간 입장 시 op=4136 이 충분히 자주 와서 전원 즉시 해소되는지,
       livestatus/ENTITY_REMAP 로그에 매핑이 찍히는지, 행에 전투력 표시되는지(1-A).
3. [x] 증상 1 (전투력 행 표시, 1-A) — 완료 (2026-06-13). `MainWindow.xaml` 이름 컬럼에
       DockPanel 로 `CombatPowerDisplay` 바인딩. 실화면 검증만 잔여.
