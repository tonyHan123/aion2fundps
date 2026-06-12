// 모든 raw event record. append-only RawEventStore 가 보관하는 immutable 데이터.
namespace Aion2FunDps.Core.EventSourcing;

/// <summary>플레이어/액터가 타겟에 가한 데미지. damage skill 1회.</summary>
public sealed record RawDamage(
    long Timestamp,
    int RawActorId,
    int TargetId,
    uint SkillCode,
    long Damage,
    bool IsCritical,
    bool IsBackAttack
) : IRawEvent;

/// <summary>DoT (지속 데미지) 한 틱. DoT skill 자체가 actor 의 데미지로 누적되지만
/// "이건 DoT" 라는 marker 가 필요해서 별도 type. Apply 시 RawDamage 와 거의 동일하게
/// 처리되지만 통계 카테고리만 다름.</summary>
public sealed record RawDotTick(
    long Timestamp,
    int RawActorId,
    int TargetId,
    uint SkillCode,
    long Damage
) : IRawEvent;

/// <summary>몹/플레이어의 현재 HP / 최대 HP 갱신. RawActorId 는 그 entity 의 id.
/// damage 와 별개로 흐르는 stream — BossTracker 가 주로 소비. ViewBuilder 는 boss view
/// 구성에 사용.</summary>
public sealed record RawHpUpdate(
    long Timestamp,
    int RawActorId,
    long CurrentHp,
    long MaxHp
) : IRawEvent;

/// <summary>소환수 spawn. RawActorId = SummonId. OwnerId / OwnerName 으로 alias 추론
/// 가능 (SUMMON_SPAWN 의 owner 정보 → AliasLog 에 alias 추가하는 source).</summary>
public sealed record RawSummonSpawn(
    long Timestamp,
    int RawActorId,        // SummonId
    int OwnerId,
    string? OwnerName,
    int? MobCode
) : IRawEvent;

/// <summary>닉네임 정보. RawActorId = canonical UserId (lobby id). Nickname / Server /
/// CombatPower 가 가용한 경우 같이. 일부는 0 (정보 없음) 일 수 있음.</summary>
public sealed record RawNickname(
    long Timestamp,
    int RawActorId,        // UserId (lobby canonical)
    string Nickname,
    bool IsSelf,
    int Server,
    int Job,
    int CombatPower
) : IRawEvent;

/// <summary>전투력 단독 갱신 (op=0092 CombatPowerUpdate). Nickname 으로 매칭.</summary>
public sealed record RawCombatPowerUpdate(
    long Timestamp,
    int RawActorId,        // 일반적으로 0 — nickname key 매칭
    string Nickname,
    int ServerId,
    int CombatPower
) : IRawEvent;

/// <summary>보스 처치 — KILL_FIRED. RawActorId = killed mob id.</summary>
public sealed record RawBossKill(
    long Timestamp,
    int RawActorId         // bossId
) : IRawEvent;

/// <summary>새 보스 등장 — NEW_BOSS_FIRED. RawActorId = boss entity id.</summary>
public sealed record RawBossEngaged(
    long Timestamp,
    int RawActorId         // bossId
) : IRawEvent;
