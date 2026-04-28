namespace Aion2FunDps.Core;

public interface IGameEvent
{
    long TimestampTicks { get; }
    uint SourceIpv4 { get; }
}
