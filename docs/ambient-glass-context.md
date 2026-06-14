# Ambient Glass 리디자인 — 컨텍스트 노트

작업 중 내린 결정과 근거. 다음 세션(사람/에이전트)이 재推론 없이 이어받기 위한 기록.

## 2026-06-13 — 전투력 가시성 + 컬럼 헤더 부활 + 숫자 프로 튠

사용자 피드백: 핑크(RoseQuartz)에서 전투력 글자가 안 보임 → 모든 테마에서 가시성 나쁨.
진단: ① 라벨 없어 174.8k가 전투력인지 총딜인지 모름 ② inline 텍스트는 테마 BG와
싸워 어디서든 묻힘 (CombatPowerBrush plum #3A2D5C 이 핑크와 hue 겹침).

**핵심 원칙 — 배지는 자기 배경을 들고 다닌다.** inline 보정으론 테마마다 또 깨짐.
라이트 테마=어두운 배지 / 다크 테마=밝은 배지로 뒤집으면 어떤 BG에서도 대비 보장.
목업 4안(A 현재 / B 라벨인라인 / C 프로스트칩 / D 솔리드배지) × 3테마 매트릭스 렌더
(tools/GlassMockup, docs/design/cp-*.png) 후 사용자 선택:
- **전투력 = C 프로스트 칩(값만)** + **'전투력' 라벨은 컬럼 헤더에 1회** (행마다 반복 X).
- 사용자 추가 요구로 헤더에 전투력/총딜/DPS/지분 4개 라벨 전부 표기 (진짜 컬럼 헤더).

적용 (빌드 0 에러):
- `Theme.*.xaml` ×3 — `CpBadgeBg/Border/TextBrush` (C 프로스트, 테마별 반전) +
  `ColHeaderBrush` 키 추가. 다크=밝은칩/골드텍스트, 핑크=어두운칩/딥와인, 크림=딥틸.
- `MainWindow.xaml` — 전투력 TextBlock → 프로스트 Border 배지(값만, CombatPowerDisplay
  빈 문자열이면 Collapsed). **2026-06-12 에 폐지했던 컬럼 헤더 행을 부활** (ScrollViewer를
  Grid[헤더行/스크롤行]로 감쌈). 헤더는 행과 동일 컬럼폭 + 좌우 인셋 14/15 로 세로 정렬.
- **총딜·DPS 컬럼 Auto → 고정폭(54/62)** — 전투력(col2 우측 끝) 위치를 행간 정렬시켜
  헤더 라벨과 칼같이 맞추기 위함. (Auto면 내용폭 따라 전투력 x가 행마다 흔들림.)
  주의: 스크롤바 등장 시에만 행이 ~17px 밀려 헤더와 미세 어긋남(허용 범위).

**숫자 프로 튠** (사용자: "프로 게임 디자이너면 총딜/DPS를 어떻게?"):
- tabular figures (`Typography.NumeralAlignment="Tabular"`) — 총딜/DPS/전투력/지분 전부.
  정적에선 차이 작지만(Rajdhani 거의 고정폭) **전투 중 숫자 틱마다 컬럼 jitter 방지**가 핵심.
- 위계 강화 — 총딜 명도 후퇴(NumSecondaryBrush 톤다운, 라이트는 BG쪽으로 밝혀 후퇴),
  단위 더 디밍(NumUnitBrush) + 단위 폰트 축소 (총딜 8.5→7, DPS 10→9). DPS만 히어로로 띄움.
- NumSecondary/NumUnitBrush 는 행 전용(다른 창 미사용) 확인 후 톤 변경.
- A/B 렌더: docs/design/number-typography-ab.png.

## 2026-06-12 — 실제 앱 적용 (Phase 1+2 완료)

칩 B-3 확정 직후 적용. 변경 파일.
- `Core/Sessions/JobClass.cs` — `GetBrightColorHex` 추가 (v2 팔레트).
- `UI/ViewModels/PlayerRowViewModel.cs` — BrightClassColorHex / DpsValue·Unit /
  TotalValue·Unit / ShareValue / ShareTier 추가. 기존 *Display 프로퍼티는 유지
  (SkillBreakdownWindow 등 호환).
- `UI/ViewModels/MainViewModel.cs` — FormatDpsKr/FormatTotalKr (만/억 단위, 값/단위 분리),
  ShareTier 계산 (파티 평균 대비 ≥1.4x=금 / ≥0.85x=은), 전투력을 행 컬럼에서 툴팁
  첫 줄로 이동, placeholder 행에도 신규 프로퍼티 세팅.
- `UI/ClassRampBrushConverter.cs` (신규) — hex→수직 램프 브러시 + hex→팁 브러시,
  hex별 캐시+Freeze (UI 틱당 allocation 0).
- `App/Assets/Fonts/Rajdhani-*.ttf` (신규) + csproj `<Resource>` 임베드 —
  single-file publish 에서도 pack URI 로 로드됨.
- `App/Themes/Theme.*.xaml` ×3 — NumFont / GlassRow* / Num* / ChipT0~2 키 추가.
  라이트 테마(RoseQuartz/SweetPastel) 값은 1차 추정치 — 실화면 보고 튜닝 필요.
- `App/MainWindow.xaml` — 행 템플릿 전면 교체 (높이 36→26), 컬럼 헤더 행 폐지
  (만/억 단위가 자체 설명), ClassRamp/ClassTip 컨버터 등록.
- 미사용이 된 것: RankBar1~8 키 + RankToBar/EdgeBrush 컨버터 리소스 (선언은 둠 —
  기존 코드 정리는 별도 커밋에서), GlassTrack* 키, DamageBar* 키 일부.
- 검증: dotnet build 오류 0, build-debug.ps1 → bin/debug-diagnostic (141MB) 산출.
- Phase 3 (전투 중 타이틀바 페이드 + 보스 HP 슬림 헤더) 미착수 — 다음 작업.

## 2026-06-12 — 디자인 방향 확정

**레퍼런스**: `ref/dps.png`(조밀형), `ref/dps2.png`(카드형), `ref/waiting*.png`(대기방 조회 모드).
A2Viewer/A2Power 계열. 따라 만들지 않고 참고만 — 사용자 요구는 "게임에 방해 안 되고,
본인·팀원 DPS가 확실히 구분되는 우리만의 디자인".

**방향 선택**: 앰비언트 글래스 (3안 중 사용자 직접 선택)
- 후보: ① 앰비언트 글래스 ② 택티컬 카드(dps2 스타일) ③ 듀얼 모드(비전투 카드 / 전투 글래스)
- 선택 근거: 게임 방해 최소화가 최우선 가치.

**핵심 원칙 — "색 = 사람, 위치 = 순위"** (사용자 질문 후 확정)
- 기존 구현은 막대 색이 순위(RankBar1~8Brush) 기준 → 순위 스왑 때마다 같은 사람 색이
  바뀌어 추적 불가 + 접전 시 색 깜빡임이 시각 방해. 이거 폐기.
- 색은 클래스에 고정(사람을 따라다님), 순위는 행 위치 + 1·2·3등 포디움 마커로만.
- 레퍼런스 양쪽·WoW Details·LOA Details 모두 같은 결론(클래스색)이라는 점도 근거.

**본인 행 구분은 색이 아니라 구조로**
- 본인 강조에 초록색(SelfDamageBarBrush)을 쓰면 클래스색 채널과 충돌
  (예: 본인이 궁성=초록이면 구분 불가). 화이트 스트로크 + 밝은 배경 + ▶ 마커로 전환.

## 데이터 가용성 (코드 확인 완료)
- 패킷에서 확보: DPS/총딜/지분/히트/크리율/백어택/스킬상세/전투력(op=0092)/클래스(op=0297 jobCode)
- **불가(2단계로 보류)**: AVG 개인 평균기록, 대기방 빌드칩 — plaync 웹 API 필요.
  A2Viewer는 `tools/a2viewer-src`의 CharacterService/PlayncClient로 구현. 패킷에 없음.
- PartyBulkInfoHandler(0x6ae2)는 2026-05-01 비활성화됨 — 친구목록 브로드캐스트 오인 이슈.
  파티 추적은 op=0297(로비) + op=0092(CP 갱신) 경로가 정상.

## 2026-06-12 — 컴팩트 모드 추가 (크기 피드백)

사용자 피드백: 인게임 합성에서 미터가 화면을 너무 차지함.
- 원인 ①: 목업 합성 캔버스가 1280인데 스샷 원본은 1920 → 1.5배 과장. 원본 해상도 합성으로 수정.
- 해결 ②: **컴팩트 모드** 신설 — 전투 중 기본. 1줄 행. 252×~250px (8인 기준).
- 풀 모드(2줄, 총딜 표시)는 비전투/정산용으로 유지, 단축키 토글.

컴팩트 v2 (같은 날 추가 피드백): "총딜이 없고 2px 언더라인은 보스전 중 식별 불가"
→ 언더라인 폐기, **클래스색 풀하이트 배경 바**(총딜 비율, 좌→우 그라데이션 0x73→0x2E
+ 끝단 2px 브라이트 엣지)로 교체. 총딜은 DPS 왼쪽 보조 위계로 복귀. 폭 286px.
행 구조: [순위][클래스배지][이름] = 정체성 그룹 / [총딜][DPS][지분] = 실시간 그룹.

v3 (같은 날, "글자 배지 별로 + 숫자 너무 별로" 피드백):
- 클래스 글자 배지 폐기 → 실제 인게임 아이콘 (Aion2FunDps.App/Assets/ClassIcons/*.png).
- 숫자 타이포 3원칙: ① 단위 한국식 통일 (K 폐기, DPS 31.2만 / 총딜 1.4억),
  ② 단위 글자 디밍 (값은 밝고 크게, 만/억/%는 0.7x 크기 + #5F6B79),
  ③ 3단 위계 (DPS 13px bold > 지분 10px semibold > 총딜 9.5px normal).
- 구현: TextBlock Inlines Run 2개 (값 Run + 단위 Run) — 실제 적용 시 컨버터 또는
  ViewModel에서 값/단위 분리 프로퍼티로.

v4 폰트 진단 (같은 날, "글씨체 자체가 별로" 피드백):
- 진단: Cascadia Mono = 개발자 터미널 폰트. 게임 HUD 숫자는 condensed/스퀘어 계열이
  정석. 모노스페이스 채택 사유였던 자릿수 정렬은 tabular figures로 해결 가능 — 도구 선택 오류.
- 후보 3종 다운로드 (전부 OFL 라이선스, 번들 재배포 가능, tools/GlassMockup/fonts/):
  Rajdhani(e스포츠 HUD 스퀘어) / Chakra Petch(SF 기계) / Saira SemiCondensed(전광판 중립).
- 4-way A/B 렌더 → docs/design/ambient-glass-font-compare.png. 추천 = Rajdhani.
- 1차 렌더 버그: FontDir 상대경로 4단계(tools\fonts) — 실제는 3단계(GlassMockup\fonts).
  전부 폴백으로 렌더돼 "다 똑같아 보임" (사용자 발견). Saira 내부 패밀리명도 "Saira".
  Fonts.GetFontFamilies 열거로 검증 후 수정.
- **사용자 선택: B. Rajdhani 확정** (2026-06-12). 실제 앱에 폰트 번들 + MonoFont 키 교체 예정.

v6 보조 숫자 트리트먼트 ("총딜/지분이 잘 안 띈다" 피드백):
- 진단: 3개 메트릭이 전부 맨몸 텍스트 — 크기 위계만으론 분리 부족. 모양(shape)·색(hue)
  채널 미사용.
- 3안 A/B 렌더 → docs/design/ambient-glass-number-compare.png.
  A=인라인(현재) / B=지분 캡슐 칩 / C=DPS 아래 총딜 마이크로 스택 + 칩 (행 +6px).
- **사용자 선택: B (지분 캡슐 칩) 확정** + "칩에 적절한 색" 요청.
- 칩 색 전략 3안 렌더 → docs/design/ambient-glass-chip-compare.png.
  B-1 골드 고정(장식) / B-2 클래스색 틴트(중복) / B-3 지분 히트(정보 — 18%↑금, 11%↑은, 중립).
- 추천 B-3. 실제 적용 시 기준은 고정%가 아니라 파티 평균 대비 상대값으로
  (4인/8인 파티 모두 동작). 사용자 선택 대기 중.
- 구현 메모: NumberStyle / ChipColorMode 변수로 변형 전환 (mockup Program.cs).

v5 바 입체화 ("색을 입체적이고 가시적으로" 피드백):
- 수직 명암 램프: 0.0=Mix(class,White,0.20) / 0.5=class / 1.0=Mix(class,Black,0.38), Opacity 0.80.
- 상단 1px 글로스(#59FFFFFF) + 끝단 2.5px 팁 Mix(class,White,0.45) + 클래스색 글로우.
- 가독 보정: 보조 텍스트 밝기 업 (총딜 #C2CBD6, 지분 #DDE4EC, 단위 #9AA5B2, 비포디움
  순위 #BCC5D1) + 텍스트 그리드에 다크 섀도. 실제 앱에선 per-row Effect 비용 때문에
  바 위 다크 스크림 or 글자 아웃라인 베이크 검토.

## 목업 전략
- 본 앱은 OnStartup에 Npcap 게이트 + 싱글톤 뮤텍스 + 패킷 파이프라인이 묶여 있어
  목업용으로 띄우기 부적합 → 독립 렌더러 `tools/GlassMockup` (WPF offscreen
  RenderTargetBitmap → PNG, 창 안 띄움). 목 데이터는 ref/dps2.png 수치 그대로 사용.
- 승인 후 실제 적용은 MainWindow.xaml + Theme.*.xaml만 수정 (ViewModel 변경 최소화 —
  필요 필드는 PlayerRowViewModel에 이미 다 있음. ClassColorHex 활용).

## 클래스 브라이트 팔레트 v2 (다크 BG용, 목업 기준)
JobClassDetector.GetColorHex는 어두운 배지용 색이라 글래스 언더라인엔 침침함.
언더라인/배지용 브라이트 변형 8색 별도 정의 (Phase 1에서 리소스 키로 승격).

v1 피드백(2026-06-12, 사용자): 살성↔마도성(보라끼리), 수호↔치유(주황/노랑),
정령↔호법(청록/파랑)이 비슷해서 구분 안 됨 → v2에서 두 가지로 해결.
1. 색상환 재배치 — 이웃 쌍 최대 분리.
2. 클래스 한 글자 배지(검/살/수/궁/마/정/치/호) 추가 — 색+글자 이중 채널, 색맹 안전.

검성 #FF5252(빨강) / 수호성 #FF7E29(진주황) / 살성 #9775FA(보라) / 궁성 #4ED44E(초록)
마도성 #F0609E(핑크) / 정령성 #20C9B0(청록) / 치유성 #FFD43B(노랑) / 호법성 #4D8DFF(진파랑)
