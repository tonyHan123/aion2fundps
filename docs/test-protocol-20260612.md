# 2026-06-12 저녁 — 업데이트 먹통 진단 테스트 프로토콜

배경: 아이온2 업데이트 후 대기방 명단·보스 DPS 표시가 타 미터 포함 전반적으로
먹통이라는 보고. 오늘 보스 레이드 1세션으로 원인을 확정하기 위한 절차.

## 사전 준비 (완료된 것)
- [x] 디버그 빌드 갱신 — `bin\debug-diagnostic\Aion2FunDps.App.exe` (SHA **A4FF...F5**)
- [x] **cold-start 정책 D 픽스 포함** — 던전 중간에 미터를 켜도, 레지스트리에 닉이 박힌
      파티원(0092 CP refresh 등 파티 채널 인증)은 COLDSTART_BOOT 없이 즉시 admit.
      검증 포인트: 던전 중간 재시작 시나리오에서 파티원 행이 뜨는지 + roster-debug 의
      `COLDSTART_REGISTRY_ADMIT` 라인에 옆파티 닉이 섞여 있지 않은지 (누수 반례 감시).
- [x] **frames-dump.log 신설** — 모든 게임 프레임의 opcode+선두 256바이트 ground-truth.
      unknown sniffer 가 못 잡는 "알려진 opcode 의 레이아웃 변경(조용한 오파싱)"까지 커버.
- [x] 기존 로그 22개 아카이브 → `%LocalAppData%\aion2fundps\logs-archive-20260612-1716`
      → 오늘 세션 로그만 깨끗하게 남음.

## 테스트 절차 (사용자)
1. 게임 실행 → **debug-diagnostic 폴더의 exe** 실행 (Release 배포본 아님 — 디버그만 전체 로그 켜짐).
2. 시나리오 각각 최소 1회. 문제가 보이는 **즉시 타이틀바 📷(MARK) 버튼 클릭** + 텔레그램에 짧은 메모
   ("마크1 = 대기방 명단 안 뜸" 식). 마크 시각이 user-marks.log 에 박혀 전 로그 교차분석 기준점이 됨.
   - [ ] 매칭 대기방 입장 → 멤버 명단 뜨는지 (op=0297 로비 경로)
   - [ ] 본인만 보스 타격 → 내 행 + DPS 뜨는지
   - [ ] 파티원도 타격 → 팀원 행 뜨는지
   - [ ] 보스 처치 → 정산 홀드/리셋 정상인지
   - [ ] (가능하면) 던전 중간 재접속/중간 합류 — cold-start 케이스
3. 세션 끝나면 그냥 미터 종료. 로그는 `%LocalAppData%\aion2fundps\logs\` 에 쌓여 있음 — 분석은 클로드가.

## 분석 시 볼 신호 (클로드용 메모)
| 증상 가설 | 1차 확인 로그 | 판정 |
|---|---|---|
| 캡처 레이어 자체 사망 | capture-health.log | recv=0 또는 drop 폭증이면 BPF/인터페이스 문제 |
| 프로토콜 대격변 (opcode 변경) | mobspawn-debug (unknown sniffer) + 대시보드 UnknownByOpcode | 새 opcode 폭증 = opcode 이동 |
| 레이아웃만 변경 (조용한 오파싱) | **frames-dump.log** vs 핸들러별 debug 로그 | op 는 오는데 parsed 값이 비거나 엉뚱하면 레이아웃 시프트 |
| 로비 명단 누락 | party-assembly-debug + party-family-debug + nickname-sweep-debug | 0297 자체가 안 오나 / 오는데 파싱 실패나 구분 |
| 닉네임 누락 | nickname-sweep-debug 에서 누락 닉 UTF-8 hex grep | 어떤 op 가 그 닉을 실어왔는지 역추적 |
| 보스 미감지 | encounter-debug + reset-debug | 0191 레이아웃/HP 폴백 경로 확인 |
| 데미지 미집계 | frames-dump 에서 전투 시간대 op 분포 | DamageHandler op 가 아예 안 오면 op 이동 |
- 분석 에이전트: `.claude/agents/dps-meter-log-analyst.md` 활용.
- frames-dump 는 전투 중 ~2MB/min — 세션 후 분석 끝나면 App.xaml.cs 의
  AllFramesLogPath 줄 주석 처리 (영구로 켜둘 로그 아님).
