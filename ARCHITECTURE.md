# ARCHITECTURE

이 파일은 코드 구조 + 데이터 흐름 + 설계 의도를 설명합니다. *"왜 이렇게 짰지"* 의 답.

## 솔루션 구조

```
Aion2FunDps.sln
├── Aion2FunDps.Capture     # Layer 1+2: NIC → 정렬된 raw bytes
├── Aion2FunDps.Protocol    # Layer 3+4: VarInt 프레이밍, LZ4, opcode dispatch
├── Aion2FunDps.Core        # Layer 5+6: 도메인 모델, 통계, 신뢰도
├── Aion2FunDps.Storage     # SQLite 로컬 영구 저장
├── Aion2FunDps.UI          # WPF 컴포넌트 (라이브러리)
├── Aion2FunDps.App         # WPF 진입점 (배포 타겟)
└── Aion2FunDps.DevConsole  # 개발 검증용 (배포 X)
```

의존성 그래프:

```
            App ──→ UI
             │       │
             ↓       ↓
       Storage ──→ Core ←── Protocol ←── Capture
                   ↑                       (no deps)
                   │
              (pure, no deps)
```

**원칙**: Core는 의존성 없음. 다른 레이어는 Core 또는 더 아래 레이어에만 의존.

## 데이터 흐름 (런타임)

```
[NCSoft 서버]
    │ TCP (varint-framed, optional LZ4)
    ▼
[Npcap 커널 BPF 필터]                     ← 64MB 버퍼
    │  src host 206.127.156.161 and tcp
    ▼
[NpcapAdapter.OnPacket]                   ← 핫패스: 수동 헤더 파싱, ArrayPool
    │  → Channel<RawPacket>
    ▼
[SequenceReorderer]                       ← Phase 1b
    │  per-srcIP TCP seq 정렬, dedup
    ▼
[FrameAssembler]                          ← Phase 1b
    │  VarInt 길이로 게임 패킷 단위 슬라이스
    ▼
[Lz4Decompressor]                         ← Phase 1b
    │  0xff 0xff 마커 감지 → LZ4 해제 → 재귀 처리
    ▼
[PacketDispatcher]                        ← Phase 1c
    │  2바이트 opcode 분기 → 핸들러
    ├── 0x04 0x38 → DamageHandler
    ├── 0x05 0x38 → DotHandler
    ├── 0x40 0x36 → SummonSpawnHandler
    ├── 0x33/44 0x36 → NicknameHandler
    ├── 0x21 0x8d → CombatBoundaryHandler
    ├── 0x00 0x8d → MobHpHandler
    └── 0x2a/2b 0x38 → BuffHandler
    ▼
[EventAttributor]                         ← Phase 1c
    │  SummonID → OwnerID 매핑
    │  DOT 틱 → 시전자 매핑
    ▼
[DpsAggregator]                           ← Phase 1c
    │  per-Player 누적 통계
    ▼
[ConfidenceEstimator]                     ← Phase 1e
    │  드랍 + 미매핑 + HP 드리프트 종합
    ▼
[WPF UI]                                  ← Phase 3
    33ms 폴링 (이벤트 푸시 X — UI 멈춰도 데이터 무손실)
```

## 핵심 설계 결정

### 1. 캡처 콜백은 데드심플

`NpcapAdapter.OnPacket`은 microseconds 안에 끝나야 함. 그래서:

- ❌ **PacketDotNet 사용 X** (allocation 발생)
- ✅ **수동 byte 오프셋으로 IP/TCP 파싱** (0 allocation)
- ✅ **ArrayPool에서 버퍼 rent** (GC 압박 0)
- ✅ **Channel.TryWrite로 즉시 enqueue** (블로킹 X)

이게 갤미터기와의 결정적 무게 차이의 출발점.

### 2. RawPacket는 struct + IDisposable

- 4 + 4 + 8 + 16 + 4 = ~36 bytes 인라인
- Buffer는 `byte[]` from `ArrayPool<byte>.Shared`
- **Dispose는 단일 소비자가 정확히 1회 호출** (Channel 단일 소비자 패턴 보장)
- 복사하지 말 것 — Channel 통해 한 곳에서만 받음

### 3. Promiscuous 모드 OFF

게임 패킷은 **우리 IP로 향하는 inbound**. 다른 PC 트래픽 보려는 거 아님.
→ Promiscuous 불필요. NIC 부하 ↓.

### 4. BPF 커널 필터로 narrowing

`src host 206.127.156.161 and tcp` — 게임 트래픽만 사용자 공간으로 전달. 커널이 99% 노이즈 차단.

### 5. Bounded Channel (4096)

용량 초과 시: TryWrite 실패 → ArrayPool 반환 + 카운터 +1.
정상 부하에선 절대 안 차고, 차면 사용자에게 노출 (신뢰도 ↓).

### 6. Layer-별 격리

Capture는 Protocol 모름. Protocol은 Core 모름 (도메인 emit만). Core는 누구도 모름.
→ 각 레이어 단위 테스트 가능. 한 곳 바꿔도 다른 곳 영향 X.

## Phase별 구현 진행도

| 레이어 | Phase | 상태 |
|---|---|---|
| 1. Pcap 캡처 | 1a | ✅ |
| 2. TCP 정렬 / dedup | 1b | ⏭️ |
| 3. VarInt 프레이밍 | 1b | ⏭️ |
| 4. LZ4 해제 | 1b | ⏭️ |
| 5. Opcode dispatch | 1c | 🔮 |
| 6. 이벤트 귀속 | 1c | 🔮 |
| 7. 통계 누적 | 1c | 🔮 |
| 8. 신뢰도 추정 | 1e | 🔮 |
| 9. SQLite 저장 | 2 | 🔮 |
| 10. WPF UI | 3 | 🔮 |

## 성능 룰 (메모리에서 영구 박제)

이 룰은 코드 첫 줄부터 강제. 위반 시 PR reject.

1. 캡처 콜백 안에서 **>1µs** 작업 금지
2. 핫패스에서 **>256 bytes/packet** 할당 금지
3. 핫패스에서 **LINQ / String 합치기 / Reflection 금지**
4. UI는 **이벤트 푸시 X, 폴링만** (33ms 디폴트)
5. SQLite 쓰기 **5초 배치 비동기**, 핫패스 절대 블로킹 X
6. WPF **블러/그림자/투명 효과 전투 중 비활성**
7. 스레드 우선순위 **Below Normal**
8. Promiscuous 모드 **항상 OFF**

## 메모리 vs 이 문서

- **이 문서 (ARCHITECTURE.md)**: 공개 — 사용자/기여자가 코드 구조 이해
- **`.claude/projects/.../memory/`**: 비공개 — 결정 배경, 운영자 컨텍스트, 시장 조사

두 곳 모두 갱신 동기화 유지.
