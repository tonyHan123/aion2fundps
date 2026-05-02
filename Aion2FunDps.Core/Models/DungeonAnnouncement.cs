namespace Aion2FunDps.Core.Models;

/// <summary>
/// Dungeon-id signal extracted from the matchmaking room broadcast (op=0297).
/// The 4-byte LE int sits right after the room-name + 1-byte member count;
/// values land in 600000-699999 for live KR content. Mapped to the
/// localized name (e.g., "무의 요람(보통)") via <see cref="DungeonDatabase"/>
/// so the meter title bar can mirror the in-game lobby header.
/// </summary>
public sealed record DungeonAnnouncement(
    int DungeonId,
    long TimestampTicks,
    uint SourceIpv4
) : IGameEvent;
