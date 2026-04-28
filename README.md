# aion2fundps

아이온2 KR 서버용 오픈소스 DPS 미터기.

> ⚠️ **개발 초기 단계 (WIP)** — 아직 사용 가능한 상태가 아닙니다. 첫 정식 릴리즈는 추후 안내됩니다.

## 차별점

- 🔓 **오픈소스 MIT** — 코드 100% 공개. 클로즈드 네이티브 DLL 의존성 없음
- 📊 **누수율 실시간 표시** — HP-데미지 교차검증으로 세션 신뢰도 표시
- ⚡ **60Hz 부드러운 UI**
- 🔄 **Hot-swap 파서** — 게임 패치 후 `parser.js` 한 파일만 갱신하면 작동
- 🏠 **로컬 우선** — 모든 사용자 데이터는 본인 PC에만 저장 (랭킹 통계는 별도, 익명)
- 🆓 **완전 무료**

## ⚠️ 백신 오탐 안내 (필수)

이 프로그램은 게임 패킷을 읽기 위해 **Npcap** (네트워크 캡처 라이브러리)을 사용합니다.
백신은 패킷 캡처 도구를 트로이잔으로 잘못 분류하는 경우가 많습니다.
**이건 진짜 바이러스가 아니라 오탐(false positive)입니다.**

### 어떻게 확인할 수 있나요?

1. **모든 소스 코드 공개** → 이 저장소에서 직접 검토 가능
2. **빌드 투명성** → GitHub Actions에서 공개 빌드, 누가 어떻게 만들었는지 검증 가능
3. **VirusTotal** → https://www.virustotal.com 에 .exe 업로드해서 다른 백신들 결과 비교
4. **직접 빌드** → 의심되시면 본인이 빌드 (BUILD.md 참조 — 추후 추가)

### 실행 안내

- **Windows Defender**: 다운로드 후 우클릭 → 속성 → "차단 해제" 체크
- **V3 / 알약**: 예외 처리에 `aion2fundps.exe` 추가
- **그래도 안 되면**: [GitHub Issues](https://github.com/tonyHan123/aion2fundps/issues)에 환경 정보와 함께 문의

## 시스템 요구사항

- Windows 10/11 64-bit
- .NET 10 Desktop Runtime ([수동 다운로드](https://dotnet.microsoft.com/download/dotnet/10.0))
- Npcap WinPcap 호환 모드 ([npcap.com](https://npcap.com/))

## 면책 조항

- 이 프로그램은 **NCSoft / 아이온2 운영사와 무관**합니다
- 게임 클라이언트나 메모리를 **수정/조작하지 않습니다** (네트워크 패킷만 수신)
- 사용으로 인한 모든 책임은 사용자 본인에게 있습니다

## 운영 종료 트리거

이 프로젝트는 다음 조건 중 하나가 충족되면 **즉시 운영을 중단**합니다:

1. NCSoft / 운영사로부터 cease & desist 요청 → 저장소 비공개 전환
2. 게임 패킷 암호화 도입 → 작별 공지 후 종료
3. 공식 미터기 정책 변경 → 상황 검토 후 결정

라이선스가 MIT이므로 운영 종료 후에도 커뮤니티에서 fork하여 유지보수 가능합니다.

## 기술 스택

- **C# / .NET 10 / WPF**
- **SharpPcap + PacketDotNet** — 네트워크 캡처 (Npcap 백엔드)
- **K4os.Compression.LZ4** — LZ4 압축 해제
- **Jint** — `parser.js` 호스팅 (hot-swap 파서)
- **Microsoft.Data.Sqlite** — 로컬 저장소

## 솔루션 구조

```
Aion2FunDps.sln
├── Aion2FunDps.Capture    # 패킷 캡처 + TCP 재조립
├── Aion2FunDps.Protocol   # LZ4 해제 + opcode dispatch + parser.js
├── Aion2FunDps.Core       # 도메인 모델 + 통계 + 신뢰도 계산
├── Aion2FunDps.Storage    # SQLite 로컬 저장
├── Aion2FunDps.UI         # WPF 컴포넌트
└── Aion2FunDps.App        # 진입점 (WPF 앱)
```

## 라이선스

[MIT](LICENSE) — © 2026 tonyHan123

## 운영자

- GitHub: [@tonyHan123](https://github.com/tonyHan123)
- 디시 아이온2 갤러리에서 동일 닉네임으로 활동
