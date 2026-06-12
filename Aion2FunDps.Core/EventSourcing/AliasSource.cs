// AliasLog 의 각 alias 가 어느 패킷 / 메커니즘에서 왔는지 표시.
namespace Aion2FunDps.Core.EventSourcing;

/// <summary>
/// AliasLog 항목의 출처. 디버깅 / 신뢰도 분석용.
///
/// 신뢰도 우선순위 (높음 → 낮음).
///   SelfNick / BulkInfo / PartyAssembly — 서버가 명시적으로 보낸 mapping. 절대 신뢰.
///   OtherNick — 일부 케이스 server 가 0 으로 줄 수 있음. 닉/직업은 신뢰.
///   SummonSpawn — owner name 매칭. owner name 자체가 truncate / 변형 가능성 있음.
///   Proxy — behavioral 추론. 자체적으로 임시 alias. 늦은 명시적 신호로 덮어써짐.
/// </summary>
public enum AliasSource
{
    SelfNick,
    BulkInfo,
    PartyAssembly,
    OtherNick,
    SummonSpawn,
    Proxy,
    /// <summary>잡 매칭 페어링 (2026-06-12) — 던전 신원 패킷 두절 시 클래스 유일성 기반
    /// 행동 추론. Proxy 급 신뢰도 — 늦은 명시적 신호로 덮어써질 수 있음.</summary>
    JobMatch,
}
