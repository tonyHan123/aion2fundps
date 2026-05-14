# aion2fundps

아이온2 KR 서버용 무료 오픈소스 DPS 미터기.

> 🚧 현재 **v0.1.0-alpha** — 첫 공개 버전입니다. 사용 중 발견되는 문제는 [Issues](https://github.com/tonyHan123/aion2fundps/issues, 혹은 discord https://discord.gg/Vxq7gZZBH 로 알려주세요.

## 주요 기능

- **실시간 DPS 측정** — 보스/던전별 자동 리셋 + 누적 합계 + 누수율 표시
- **파티원 자동 인식** — 8 직업 클래스 아이콘 + [server] 표기
- **스킬 상세 보기** — 행 클릭 → 스킬별 사용 횟수 / 크리율 / 총 피해량 / 비율
- **테마 2종** — 다크+블루 (기본), Sweet Pastel (크림+핑크)
- **단축키 커스터마이징** — 리셋 / 최소화 등 직접 지정
- **본인 컴퓨터 안에서만** — 측정 데이터 외부 송신 0건. 서버 운영 0대.
- **컴팩트 모드** — 타이틀바만 남기는 미니 모드

## 차별점

- 🔓 **오픈소스 MIT** — C# UI/로직 + C++ 패킷 파서 모두 본 저장소에 소스 공개. 외부 폐쇄 의존성 없음
- 🛡️ **데이터 누출 0** — 측정 데이터 어디로도 안 보냄. 100% 로컬
- 💸 **완전 무료** — 광고/구독/결제 없음
- 📊 **신뢰도 표시** — HP-데미지 교차검증 누수율 실시간 표시
- ⚡ **게임에 0 영향** — 메모리 100MB 이하, CPU 3% 이하 (전투 중), 게임 FPS 영향 1 이하 보장

## 성능 commitment



| 지표 | 목표 |
|---|---|
| CPU (게임 idle) | < 1% |
| CPU (격렬한 전투 중) | < 3% |
| 메모리 (Working Set) | < 100 MB |
| 게임 FPS 영향 | < 1 FPS |
| 프레임 시간 영향 | < 0.1 ms |

순수 C# + WPF + 네이티브 C++ 파서로 구성. JVM/Electron/Chromium/Flutter 전혀 사용하지 않음. 캡처 스레드는 BPF 커널 필터 + bounded channel + ArrayPool 으로 hot-path 할당 0.

## ⚠️ 백신 오탐 안내 (필수)

이 프로그램은 게임 패킷을 읽기 위해 **Npcap** (네트워크 캡처 라이브러리) 를 사용합니다. 백신은 패킷 캡처 도구를 트로이잔으로 잘못 분류하는 경우가 많습니다. **이건 진짜 바이러스가 아니라 오탐 (false positive) 입니다.**

### 어떻게 확인할 수 있나요?

1. **모든 소스 코드 공개** → 이 저장소에서 직접 검토 (C# + C++ 둘 다)
2. **VirusTotal** → https://www.virustotal.com 에 .exe 업로드해서 다른 백신들 결과 비교
3. **직접 빌드** → 의심되시면 본인이 빌드 ([BUILD.md](BUILD.md))
4. **ILSpy / dotPeek** → exe 분해해서 .NET 코드가 저장소와 일치하는지 확인 (Release 빌드는 ConfuserEx 2 로 난독화되어 있어 일부 메소드는 가독성 떨어질 수 있음 — 의심되시면 직접 빌드 비교 권장)

### 백신 오탐 시 대처

- **Windows Defender**: 다운로드 후 우클릭 → 속성 → "차단 해제" 체크
- **V3 / 알약**: 예외 처리에 `aion2fundps.exe` 추가
- **그래도 안 되면**: [Issues](https://github.com/tonyHan123/aion2fundps/issues) 에 환경 정보와 함께 문의

## 시스템 요구사항

- Windows 10/11 64-bit
- **Npcap** WinPcap 호환 모드 — 미설치 시 첫 실행 때 자동 안내
- .NET 10 런타임은 exe 안에 포함됨 (별도 설치 불필요)

## 사용법

1. [Releases](https://github.com/tonyHan123/aion2fundps/releases) 에서 최신 `Aion2FunDps.App.exe` 다운로드
2. 더블클릭 → Npcap 미설치 시 안내 다이얼로그 → [Npcap 다운로드 페이지 열기]
3. https://npcap.com 에서 Npcap 설치 (꼭 **'Install Npcap in WinPcap API-compatible Mode'** 체크)
4. 미터 다시 실행 → 게임 띄우고 사용

화면 상단의 ⚙ (설정) → 테마 변경, 단축키 지정, 자동 리셋 토글, 컴팩트 모드 등.

### 단축키 (기본값)

| 키 | 동작 |
|---|---|
| `Ctrl+R` | 세션 리셋 |
| `클릭`(파티원 행) | 스킬 상세 보기 |

⚙ 에서 자유롭게 변경 가능. 시스템 단축키 (Win32 RegisterHotKey) 라 게임 창에 포커스 있어도 동작.

## 면책 조항

- 이 프로그램은 **NCSoft / 아이온2 운영사와 무관**합니다
- 게임 클라이언트나 메모리를 **수정/조작하지 않습니다** (네트워크 패킷만 수신)
- 사용으로 인한 모든 책임은 사용자 본인에게 있습니다

## 운영 종료 트리거

이 프로젝트는 다음 조건 중 하나가 충족되면 **즉시 운영을 중단**합니다:

1. NCSoft / 운영사로부터 cease & desist 요청 → 저장소 비공개 전환
2. 게임 패킷 암호화 도입 → 작별 공지 후 종료
3. 공식 미터기 정책 변경 → 상황 검토 후 결정

라이선스가 MIT 이므로 운영 종료 후에도 커뮤니티에서 fork 하여 유지보수 가능합니다.

## 신스킬 / 신컨텐츠 갱신

게임 패치로 새 스킬이 나오면 미터에 일시적으로 `#코드` 식으로 표시될 수 있습니다 (DPS 측정 자체엔 영향 0). [Issues](https://github.com/tonyHan123/aion2fundps/issues) 에 [신스킬 제보] 템플릿으로 알려주시면 다음 릴리즈에 반영됩니다.

## 솔루션 구조

```
aion2fundps/
├── Aion2FunDps.Capture          # Npcap 패킷 캡처 + TCP 재조립
├── Aion2FunDps.Protocol         # opcode dispatcher + 12 핸들러 (managed)
│   └── NativeEngine             # C++ 엔진과의 P/Invoke 어댑터
├── Aion2FunDps.Engine           # C++ 패킷 파서 DLL (LZ4 + varint + 12 핸들러)
├── Aion2FunDps.Core             # 도메인 모델 + 통계 + 신뢰도 + 카탈로그
├── Aion2FunDps.Storage          # 정적 게임 데이터 (몹/던전/버프)
├── Aion2FunDps.UI               # WPF ViewModel + 클래스 아이콘 팩토리
├── Aion2FunDps.App              # WPF 진입점 + 창 + 테마 + 단축키
├── Aion2FunDps.NativeRegression # C++/C# 엔진 동등성 회귀 검증 도구
└── Aion2FunDps.DevConsole       # 라이브 캡처 콘솔 (개발용)
```

**Native vs Managed 엔진:**
패킷 파싱은 두 경로가 모두 구현되어 있고 byte-for-byte 동일한 이벤트 스트림을 만들도록 회귀 검증됩니다 (`Aion2FunDps.NativeRegression`). 알파에서는 검증된 managed 경로가 default 로 동작하고, native 경로는 `App.UseNativeEngine` 플래그로 토글합니다 — RE 저항 강화가 필요해질 때 native 로 전환할 수 있도록 양 경로를 유지합니다.

## 빌드 방법

[BUILD.md](BUILD.md) 참조. Release 빌드는 ConfuserEx 2 난독화 + 단일 exe 패킹.

## 라이선스

[MIT](LICENSE) — © 2026 tonyHan123

써드파티 데이터/알고리즘 사용 표기는 [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) 참조.

## 운영자

- GitHub: [@tonyHan123](https://github.com/tonyHan123)
- discord : https://discord.gg/Vxq7gZZBH
