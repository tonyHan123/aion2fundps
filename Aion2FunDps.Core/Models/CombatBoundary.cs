namespace Aion2FunDps.Core.Models;

public sealed record CombatBoundary(
    int MobId,
    long TimestampTicks,
    uint SourceIpv4
) : IGameEvent;
