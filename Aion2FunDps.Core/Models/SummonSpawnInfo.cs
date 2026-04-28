namespace Aion2FunDps.Core.Models;

public sealed record SummonSpawnInfo(
    int SummonId,
    int OwnerId,
    int? MobCode,
    long TimestampTicks,
    uint SourceIpv4
) : IGameEvent;
