namespace Aion2FunDps.Core.Models;

public sealed record MobHpUpdate(
    int MobId,
    long CurrentHp,
    long TimestampTicks,
    uint SourceIpv4
) : IGameEvent;
