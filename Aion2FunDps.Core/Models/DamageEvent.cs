namespace Aion2FunDps.Core.Models;

[Flags]
public enum SpecialDamageFlags : byte
{
    None     = 0x00,
    Back     = 0x01,
    Unknown1 = 0x02,
    Parry    = 0x04,
    Perfect  = 0x08,
    Double   = 0x10,
    Endure   = 0x20,
    Unknown4 = 0x40,
}

public sealed record DamageEvent(
    int ActorId,
    int TargetId,
    int Damage,
    uint SkillCode,
    int Type,
    SpecialDamageFlags Specials,
    int Loop,
    bool IsDot,
    long TimestampTicks,
    uint SourceIpv4
) : IGameEvent
{
    public bool IsCritical => Type == 3;
    public bool IsBackAttack => (Specials & SpecialDamageFlags.Back) != 0;
}
