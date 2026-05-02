namespace Aion2FunDps.Core.Models;

public sealed record NicknameInfo(
    int UserId,
    string Nickname,
    bool IsSelf,
    int Server,
    int Job,
    int CombatPower,         // 0 if not extractable (truncated packet, etc.)
    long TimestampTicks,
    uint SourceIpv4,
    /// <summary>
    /// True when emitted by a packet whose semantics is "party member roster"
    /// or "party member live status". The aggregator uses
    /// this to pre-create PlayerStats so dungeon-entry rosters appear immediately
    /// — without it, party members only show up after their first damage tick.
    /// Always false for OTHER_NICK / SELF_NICK (visible-cone based; pre-creating
    /// those would surface town strangers in the leaderboard).
    /// </summary>
    bool IsPartyMember = false,
    /// <summary>
    /// True on the first NicknameInfo of a fresh roster broadcast — a signal to
    /// the aggregator to clear its previous _partyMembers set before processing.
    /// Without this, leaving one matchmaking room and entering another would
    /// leave the old room's members displayed alongside the new room's.
    /// Set only by PartyAssemblyHandler / PartyBulkInfoHandler on the leading
    /// emit of each parsed packet.
    /// </summary>
    bool IsRosterStart = false,
    /// <summary>
    /// Room/group identifier extracted from the broadcast packet (uint32 LE
    /// room id at offset 2 for op=0297). Used by the aggregator to detect room changes: when the
    /// GroupId differs from the last seen, the previous _partyMembers set is
    /// wiped before adding the new roster. Same GroupId on repeat broadcasts
    /// (op=0297 fires at ~3Hz) is idempotent — no wipe.
    /// 0 for non-roster events (OTHER_NICK / SELF_NICK).
    /// </summary>
    int GroupId = 0
) : IGameEvent;
