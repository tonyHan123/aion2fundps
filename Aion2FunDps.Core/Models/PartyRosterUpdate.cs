namespace Aion2FunDps.Core.Models;

/// <summary>
/// Reliability tier of a roster snapshot — drives how the aggregator reacts
/// to "member missing from this snapshot". See <see cref="Sessions.RoomLifecycleTracker"/>.
/// </summary>
public enum RosterConfidence
{
    /// <summary>
    /// op=0297 (matchmaking broadcast) and op=6ae2 (zone-load bulk). Each
    /// snapshot is authoritative for the room it names — both adds and
    /// removes apply immediately.
    /// </summary>
    Strong,
    /// <summary>
    /// op=0197 (multi-room lobby preview). Adds apply immediately; removes
    /// require multiple consecutive weak snapshots agreeing on the absence
    /// before they fire, because the parser can miss members in an otherwise
    /// valid room block within the noisy multi-room layout.
    /// </summary>
    Weak,
}

/// <summary>
/// Atomic snapshot of a party-roster broadcast.
/// The aggregator delegates application to <see cref="Sessions.RoomLifecycleTracker"/>
/// which reconciles strong + weak snapshots into a single trusted state.
/// </summary>
/// <param name="ContainsSelf">
/// True if the user's own actor was present in this snapshot's parsed members.
/// Required for weak removal: a Weak snapshot WITHOUT self is structurally
/// suspect (the parser may have missed records, or this is a near-duplicate
/// block of the same roomId where the parse only caught a partial subset).
/// Treating such a snapshot as authoritative for "member is gone" can falsely
/// remove real members. Strong snapshots ignore this flag.
/// </param>
public sealed record PartyRosterUpdate(
    int GroupId,
    IReadOnlyList<NicknameInfo> Members,
    long TimestampTicks,
    uint SourceIpv4,
    RosterConfidence Confidence = RosterConfidence.Strong,
    bool ContainsSelf = true
) : IGameEvent;
