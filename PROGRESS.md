# PROGRESS

이 파일은 프로젝트 진행 상황 + 다음 할 일을 기록합니다. 시간 지나서 돌아왔을 때 이거 먼저 읽으세요.

## 현재 위치

**Phase 1c-iii 완료** (2026-04-28). 데이터 레이어 사실상 완성:
- 캡처 → 시퀀스 정렬 → 프레이밍 → opcode 분기 → 도메인 이벤트 → 통계 누적 ✅
- 8개 opcode 핸들러 (DAMAGE, DOT, MOB_HP, COMBAT_BOUNDARY, SELF/OTHER_NICK, SUMMON, BUFF는 추후)
- mob_data.json + skills.json 통합 (몹 6,384 / 스킬 8,211)
- 타겟-공유 필터로 옆 사람 자동 제외
- 보스 모드 자동 감지 (HP ≥ 500K)
- 한글 스킬명 표시
- 라이브 검증 완료 (필드 솔로 사냥에서 본인만 leaderboard 등장)

## 마일스톤 진행도

### ✅ Phase 0 — 정찰

| 단계 | 결과 | 산출물 |
|---|---|---|
| 0.0 PoC: SharpPcap 동작 확인 | 0 드랍, 2734 패킷 / 30s | `PcapPoc/` |
| 0.1 게임 서버 IP 확보 | `206.127.156.161` (NCSoft, AS142005) | `reference_aion2_protocol.md` |
| 0.2 프로토콜 스펙 확보 | TK-open-public 분석, opcode 8개 + VarInt + LZ4 | `reference_aion2_protocol.md` |
| 0.3 v1 스펙 확정 | 무료 + 기부, GitHub 공개, 닉네임 운영 | `project_*.md` 메모리 |
| 0.4 솔루션 골격 + 첫 commit | 6 프로젝트 + GitHub 저장소 | commit `8cce9ab` |

### ✅ Phase 1a — Capture 레이어

| 항목 | 결과 |
|---|---|
| 파일 5개 작성 | `Aion2FunDps.Capture/*.cs` |
| 검증 (드랍 0개) | 2146 packets / 143 KB / 60s |
| Commit | `bbf38ad` |

### ✅ Phase 1b — TCP 정렬 + VarInt 프레이밍 + LZ4 감지

| 항목 | 결과 |
|---|---|
| 게임 패킷 추출 | 1670개 / 60초 활성 전투 |
| 4-tuple flow 분리 | flow=1로 정확히 식별 |
| Hold overflow | 0 (TCP 재정렬 정상) |
| Malformed | 0 (VarInt 프레이밍 정상) |
| 알려진 opcode 인식 | DAMAGE 54, MOB_HP 93, COMBAT 23, SUMMON 13, DOT 2, BUFF 3 |
| LZ4 압축 마커 감지 | 4 |
| Commit | `c6ff67e` (TK -4 공식 적용) |

**핵심 학습**: TK-open-public의 `realLength = value + length - 4` 공식. 4바이트 protocol artifact 보정 필요.

### ⏭️ Phase 1c — 다음 (LZ4 해제 + opcode dispatch + 도메인 모델)

목표: 게임 패킷 → 의미 있는 도메인 이벤트 (DamageEvent 등)

| 작업 | 위치 |
|---|---|
| `Lz4Decompressor.cs` 실제 해제 | `Aion2FunDps.Protocol/` |
| `PacketDispatcher.cs` opcode 분기 | `Aion2FunDps.Protocol/` |
| `Models/DamageEvent.cs` 등 도메인 | `Aion2FunDps.Core/` |
| Handlers/* (DAMAGE, MOB_HP, NICK, SUMMON 등) | `Aion2FunDps.Protocol/` |
| DevConsole: 실제 데미지 이벤트 출력 | `Aion2FunDps.DevConsole/` |

**검증**: 콘솔에 *"actor=12345 → target=67890, dmg=98765, crit=true"* 같은 실제 파싱 결과 출력.
### 🔮 Phase 1d — 통합 (App console 모드)
### 🔮 Phase 1e — 신뢰도 표시 (HP-데미지 교차 검증)
### 🔮 Phase 2 — 도메인 + 통계 + Storage
### 🔮 Phase 3 — WPF UI (오버레이 + 신뢰도 배지)
### 🔮 Phase 4 — 배포 인프라 (Velopack, GitHub Releases)
### 🔮 Phase 5 — Soft launch (친구 5~10명)
### 🔮 Phase 6 — 디시 갤러리 출시
### 🔮 Phase 7 — v2 홈페이지 (Cloudflare Workers + D1)

## 발견된 미해결 과제

| 과제 | 우선순위 | 메모 |
|---|---|---|
| **CP (전투력) 패킷 opcode 발굴** | 중 | A2Viewer가 추출했으니 분명 있음. Phase 1b/1c 중 hex dump 분석으로 발굴 |
| 새 opcode 발견 시 자동 로깅 | 중 | Jint hot-swap parser와 함께 |
| Code signing 인증서 | 낮 | 사용자 1000명+ 되면 검토 ($100~600/년) |
| WPF UI 디자인 톤 | 낮 | Phase 3 시작 시 결정 |

## 결정 기록 (Decision Log)

체계적 관리 핵심 — 왜 이렇게 결정했는지 기록 (변경 시 여기 추가).

### 2026-04-28
- **저장소 이름**: `aion2fundps` ("aion2" + "fun" + "dps")
- **운영자 닉네임**: `tonyHan123` (GitHub 기존 계정 사용, display name 그대로 노출)
- **라이선스**: MIT
- **언어/프레임워크**: C# / .NET 10 / WPF (JVM/Electron/Flutter 명시적 거부)
- **자동 업로드 정책**: 옵트인 다이얼로그 X, off 토글 X. 스마트 필터 통과 시 자동 업로드. 통계 집계만 표시 → PIPA 적용 X.
- **업로드 범위**: 본인 + 파티원 모두 (class × CP 통계 풍부도 위해)
- **인프라 모델**: GitHub-as-infrastructure. 자체 서버 0원.
- **종료 트리거**: NCSoft cease & desist / 패킷 암호화 / 공식 정책 변경
- **성능 예산**: <3% CPU, <100MB RAM, <1 FPS 영향

## 다음 작업 시작 시 첫 명령

```bash
cd c:\Users\phdbl\Desktop\dps
git status
git log --oneline -5
```

PROGRESS.md를 다시 읽고, ⏭️ 마크된 다음 단계로 진행.
