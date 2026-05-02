namespace Aion2FunDps.Core.Models;

/// <summary>
/// Encounter banner packet (opcode 0x01 0x91) — broadcast every ~1 second while
/// a boss-grade encounter is active in the player's vicinity. Contains the
/// boss's MobCode but not its entityId; the link is reconstructed at
/// <c>NewBossDetected</c> time using the most recent announcement.
/// </summary>
public sealed record EncounterAnnouncement(
    int MobCode,
    long TimestampTicks,
    uint SourceIpv4
) : IGameEvent;
