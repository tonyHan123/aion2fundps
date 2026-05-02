namespace Aion2FunDps.Core.Models;

/// <summary>
/// Standalone CP broadcast — opcode 0x00 0x92. Identified by reverse-engineering
/// A2Viewer's PartyStreamParser.ScanCombatPowerRaw (May 2026): the game sends
/// these periodically while the user is in a dungeon, carrying the freshest CP
/// for nearby/party characters keyed by (nickname, serverId). Without handling
/// these we never refresh CP after the matchmaking-room op=0297 broadcasts stop
/// firing, which surfaced as "보스 때리니까 파티원 전투력이 안 떠".
///
/// Identification is by NICKNAME, not entity_id, because op=0x00 0x92 doesn't
/// carry entity_id at all. Aggregator resolves nickname → existing registry
/// entry to update CP without creating new leaderboard rows.
/// </summary>
public sealed record CombatPowerUpdate(
    string Nickname,
    int ServerId,
    int CombatPower,
    long TimestampTicks,
    uint SourceIpv4
) : IGameEvent;
