namespace Aion2FunDps.Core.Models;

/// <summary>
/// Signal that the user has left their current matchmaking room. Emitted by
/// the dispatcher when it sees op=0x1d 0x97 — observed in A2Viewer's
/// PartyStreamParser as the "1D 97 퇴장" leave packet. The aggregator clears
/// _partyMembers and tells the room tracker to drop its state, otherwise
/// the previous room's roster lingers in the leaderboard until the user
/// joins a new room or hits the manual reset.
/// </summary>
public sealed record PartyLeft(
    long TimestampTicks,
    uint SourceIpv4
) : IGameEvent;
