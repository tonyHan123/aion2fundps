# Ambient Glass 리디자인 체크리스트

목표: 전투 중 게임을 방해하지 않으면서 본인·팀원 DPS를 0.5초 글랜스로 구분하는 오버레이.

## Phase 0 — 목업 (승인 게이트) ✅
- [x] 독립 목업 렌더러 작성 (`tools/GlassMockup`) — Npcap 없이 PNG 출력
- [x] 목업 렌더: 클로즈업 + 인게임 합성 + 폰트 4-way + 숫자 트리트먼트 3-way + 칩 색 3-way
- [x] 사용자 승인 — 최종 확정: 컴팩트 1줄 행 / Rajdhani / 입체 바 / 지분 칩 B-3(히트)

## Phase 1 — 테마/색 체계 ✅
- [x] 클래스 브라이트 팔레트 v2 → `JobClassDetector.GetBrightColorHex` (Core)
- [x] 색=사람(클래스) 고정 — 새 행 템플릿은 RankBar1~8 순위색 미사용
- [x] 포디움(1·2·3) = 순위 숫자 색만 (기존 PodiumGold/Silver/Bronze 재활용)
- [x] 3개 테마 키 계약 유지 — NumFont/Glass*/Num*/ChipT* 키를 3개 테마 모두에 추가
- [x] Rajdhani(OFL) 번들 — `Assets/Fonts/*.ttf` Resource 임베드

## Phase 2 — 행 템플릿 (MainWindow.xaml) ✅
- [x] 컴팩트 1줄 행: [▶+순위][클래스아이콘][이름] | [총딜][DPS][지분칩]
- [x] 클래스색 입체 막대 — 수직 명암 램프 + 상단 글로스 + 끝단 팁 (`ClassRampBrushConverter`)
- [x] 숫자 값/단위 Run 분리 + 한국식 만/억 단위 (`FormatDpsKr`/`FormatTotalKr`)
- [x] 지분 히트 칩 B-3 — 파티 평균 대비 상대 티어 (`ShareTier`: ≥1.4x 금 / ≥0.85x 은)
- [x] 본인 행 = 구조 구분 (GlassSelfStroke + ▶), 클래스색 채널 침범 없음
- [x] 컬럼 헤더 행 폐지 (만/억 단위가 자체 설명) — 던전 이름 줄만 유지
- [x] 전투력 → 행 툴팁으로 이동 (행 밀도 절감)

## Phase 3 — 전투 모드 자동 전환 (미착수)
- [ ] 전투 감지 시 타이틀바 페이드아웃 (마우스 오버 시 복귀)
- [ ] 헤더 = 보스명 + 타이머 + 슬림 HP바 통합 (목업의 전투 헤더)
- [ ] 전투 중 외곽 보더/배경 투명화, 비전투 시 복귀

## Phase 4 — 검증
- [x] dotnet build 통과 (오류 0)
- [ ] build-debug.ps1 디버그 exe 산출 (진행 중)
- [ ] 8인 풀파티 / 1인 솔로 / 빈 상태 3케이스 확인 (사용자 실게임)
- [ ] 3개 테마 전환 확인 — 라이트 테마(RoseQuartz/SweetPastel) 글래스 값은 1차 추정치,
      실화면 보고 튜닝 필요
- [ ] 실게임 오버레이 테스트 (사용자)
