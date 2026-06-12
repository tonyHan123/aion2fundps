# DPS 통계 사이트 — Phase A 설계 (2026-06-12)

사용자 결정.
- 집계(평균/분포)는 **파티 전원** 데이터 사용.
- 원정별 개인 기록·닉네임 조회 공개는 **미터를 직접 사용 + 개인정보이용동의한 유저만**.
- 호스팅: 무료 티어로 시작 (Supabase=DB/API + Vercel=웹) — 트래픽 늘면 VPS 이전.

## 데이터 모델

### fights — 전투 1건 (파티 단위, dedup 후 canonical)
| 컬럼 | 설명 |
|---|---|
| fight_id | uuid PK |
| fight_fingerprint | **unique** — dedup 키 (아래 산식) |
| dungeon_id / difficulty / boss_code | dungeons.json / mobs.json 코드 재사용 |
| kill_ts / duration_sec / party_size | |
| uploader_count | 같은 전투를 보고한 미터 수 (중복 병합 횟수) |
| meter_version / schema_version | 버전별 필드 진화 대응 |

**fingerprint = sha256(dungeon_id | boss_code | server_id | floor(kill_ts/20s) | sorted(player_keys))**
→ 8인 파티에서 4명이 미터를 켜도 1건. 첫 업로드가 canonical, 이후 중복은
uploader_count 증가 + 빈 필드 보충만.

### fight_records — 전투 × 플레이어
| 컬럼 | 설명 |
|---|---|
| fight_id FK / player_key | player_key = sha256(server_id + nickname) |
| nickname / server_id | 저장은 하되 **공개는 consent 게이트** 통과 시만 |
| job_class / combat_power / cp_bucket | cp_bucket = floor(cp / 50_000) — 50k 버킷 |
| total_damage / dps / share_pct / crit_rate / back_rate | |
| is_uploader_self | 업로더 본인 행 여부 (동의 추적용) |

### users — 동의/공개 관리
| 컬럼 | 설명 |
|---|---|
| install_token | 미터 설치별 발급 (업로드 인증 + rate limit 키) |
| verified_nickname / server_id | 창 제목 self-nick (이미 구현돼 있음) 기반 |
| consent_public_profile / consented_at | 미터 설정창 체크박스 → 닉네임 조회 공개 ON |
| opted_out_at | 옵트아웃 — 조회 페이지에서 즉시 제외 + 기존 기록 비공개 전환 |

## API (FastAPI)
- `POST /v1/fights` — 미터 업로드 (install_token 헤더). 서버 검증:
  share 합 ≈100%, dps/cp 상한 캡(조작 컷), 버전 allowlist, 토큰별 rate limit.
- `GET /v1/stats/dungeons/{id}?difficulty=` — 클래스×cp_bucket 집계
  (count / avg / p25 / p50 / p75 / p90). 익명 — 동의 불필요.
- `GET /v1/players/{server}/{nickname}` — **consent_public_profile=true 만 200**,
  아니면 404 (존재 여부도 안 알려줌). 원정별 기록 + 평균.

집계는 materialized view (dungeon × difficulty × boss × class × cp_bucket),
주기 refresh — 조회 트래픽이 raw 테이블 안 건드림.

## 웹 (2페이지로 시작)
1. **던전 통계** — 던전/난이도 선택 → 클래스별 cp 50k 버킷 분포 차트
   (percentile 밴드 + 평균 + 표본수). 표본수 미달 버킷(n<10)은 회색 처리.
2. **닉네임 조회** — 동의 유저만. 원정별 기록 리스트 + 개인 평균 + 클래스 동버킷
   평균 대비 위치 (상위 몇 %).

## 미터 쪽 작업 (Phase A 후반)
- 보스 킬 확정 시점(정산 홀드 진입)에 레코드 생성 → 백그라운드 업로드 (실패 시 로컬 큐 재시도).
- 설정창: 업로드 ON/OFF + 개인정보이용동의 체크 (기본 OFF — 동의 안 하면 업로드 자체 안 함
  vs 익명 업로드만 — **결정 필요**: 익명 업로드 기본 ON이 통계엔 유리하나 약관 명시 필수).
- 참고 구현: tools/a2viewer-src `CombatUploader` / `ConsentForm`.

## 선행 조건
- 오늘(2026-06-12) 저녁 업데이트 먹통 진단 먼저 — 파싱이 깨진 상태로 업로더를 붙이면
  오염 데이터만 쌓임. frames-dump 분석 → 파서 복구 → 그 다음 Phase A 구현.
