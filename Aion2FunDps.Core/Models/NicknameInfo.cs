namespace Aion2FunDps.Core.Models;

public sealed record NicknameInfo(
    int UserId,
    string Nickname,
    bool IsSelf,
    int Server,
    int Job,
    long TimestampTicks,
    uint SourceIpv4
) : IGameEvent;
