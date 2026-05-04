using System.Collections.Concurrent;
using Aion2FunDps.Core.Models;
using Aion2FunDps.Core.Repositories;

namespace Aion2FunDps.Core;

/// <summary>
/// Maps any incoming entity_id (lobby/op=0297, SELF_NICK userId, dungeon damage
/// actor id, OTHER_NICK probe id, raid bulk id) to a single canonical Player
/// entry keyed by the player's nickname.
///
/// Why aliasing: Aion 2 KR issues different entity_ids for the same character
/// across id-spaces (lobby vs dungeon vs SELF_NICK vs raid bulk). Earlier
/// designs stored a separate <see cref="NicknameEntry"/> per entity_id and
/// asked the UI to dedupe by nickname at render time, which produced cascading
/// bugs (CP shown for some rows but not others, "나" placeholder + 짭호 row
/// duplicated, stale dungeon ids surviving room changes). The aliasing model
/// collapses all those id-spaces to one canonical entry; PlayerStats / party
/// membership / leaderboard rows all key off canonical, so duplicates cannot
/// arise by construction.
/// </summary>
public sealed class NicknameRegistry
{
    /// <summary>Canonical entity_id → entry (one per nickname). Bounded at
    /// 10k entries with FIFO eviction so long sessions in busy zones (where
    /// thousands of unique characters drift past) don't grow this without
    /// bound. Self's canonical is Pin()ned once identified so the user's
    /// own row is never evicted, even if 10k+ other nicknames stream in
    /// over hours of city loitering.</summary>
    private readonly BoundedConcurrentDictionary<int, NicknameEntry> _entries = new(10_000);
    /// <summary>Nickname → canonical entity_id. Bounded the same way as
    /// <see cref="_entries"/>. Self's nickname is Pin()ned alongside the
    /// canonical entry so the reverse lookup also survives eviction
    /// pressure. Drift between the two dicts under the cap is benign:
    /// a stale nickname-only entry resolves to a missing canonical and
    /// the next Register call re-establishes both atomically.</summary>
    private readonly BoundedConcurrentDictionary<string, int> _nicknameToCanonical
        = new(10_000, StringComparer.Ordinal);
    /// <summary>Any entity_id → canonical entity_id. Includes self-loops for canonicals.
    /// ConcurrentDictionary for the same reason as <see cref="_nicknameToCanonical"/>.
    /// Bounded with FIFO eviction at 10k entries — this is the fastest-growing
    /// collection in the registry (one entry per unique spawn id observed),
    /// which over hours of play in busy zones would otherwise hit tens of
    /// thousands. Active members and self stay alive because the lobby/party
    /// broadcasts re-register them every few seconds (refreshing their
    /// position in the FIFO order). Stale aliases from past dungeons are the
    /// ones that get dropped — they're never queried again.</summary>
    private readonly BoundedConcurrentDictionary<int, int> _aliases = new(10_000);

    /// <summary>Canonical entity_id of the user's own character. Set when
    /// <see cref="Register"/> processes a NicknameInfo with IsSelf=true.</summary>
    public int? SelfUserId { get; private set; }
    public string? SelfNickname { get; private set; }

    /// <summary>
    /// Frequency table for SelfNickname inference when SELF_NICK never fires
    /// this session (e.g., the user was already logged in before the meter
    /// started). The user is the only character that appears in every party
    /// roster broadcast they receive, so the most-frequent nickname across
    /// op=0297 packets is self with extremely high confidence after a few
    /// broadcasts. Solo broadcasts (parsed=1) get extra weight — being alone
    /// in a room is a strong self-signal that doesn't apply to anyone else.
    /// </summary>
    private readonly Dictionary<string, int> _selfNickCandidates = new();
    /// <summary>
    /// Per-nickname set of groupIds we've seen the nickname in. Only the
    /// user can appear in multiple matchmaking rooms (their own room across
    /// time, plus any browsed/joined room) — every other player's nickname
    /// is locked to one groupId per session. Once a nickname's groupId-set
    /// reaches size 2 we adopt them as self with full confidence; this is
    /// MUCH faster than the score-based path which can't differentiate when
    /// every member of a stable room ties on attendance count.
    /// </summary>
    private readonly Dictionary<string, HashSet<int>> _nickGroupIds = new();
    /// <summary>
    /// Lobby server id of the user's own character. Set by matching SELF_NICK's
    /// nickname against later op=0297 member records — SELF_NICK and op=0297
    /// live in different id spaces (user_id vs entity_id) so direct id match
    /// doesn't work, but the nickname is the same in both. Without this the
    /// UI can't append "[server]" to cross-server members because we never
    /// learn which server the user is on.
    /// </summary>
    public int? SelfServerId { get; private set; }

    /// <summary>
    /// Resolves any entity_id (lobby/dungeon/SELF_NICK/etc.) to its canonical
    /// representation. Returns null if this entity_id has never been registered
    /// — caller may use the raw id as a temporary self-canonical and let a
    /// later <see cref="Register"/> alias it once the nickname is learned.
    /// </summary>
    public int? ResolveCanonical(int entityId) =>
        _aliases.TryGetValue(entityId, out var c) ? c : null;

    /// <summary>
    /// Registers a nickname-bearing event (SELF_NICK / OTHER_NICK / op=0297 /
    /// op=01 92 LiveStatus / op=6ae2 raid bulk) and returns the canonical
    /// entity_id under which this player's stats live.
    ///
    /// If the nickname is already known under a different entity_id, the new
    /// entity_id becomes an alias to the existing canonical (NOT a separate
    /// entry). All future damage / roster / display lookups using either
    /// entity_id resolve to the same canonical → one Player, one row, one set
    /// of stats.
    /// </summary>
    public int Register(NicknameInfo info)
    {
        int incoming = info.UserId;
        // Normalize Korean nicknames to NFC at the entry boundary. Different
        // packet handlers can deliver the same nickname in NFC vs NFD form
        // (Hangul composed vs decomposed) — without this normalization, the
        // string keys in _nicknameToCanonical / _nickGroupIds get duplicated
        // entries that look identical but hash to different buckets, breaking
        // multi-room self-inference. Wire-confirmed 2026-05-04: 짭호 in two
        // separate Strong rosters failed to match in _nickGroupIds, leaving
        // SelfUserId null even though the nick clearly appeared across rooms.
        string? nickname = NormalizeNickname(info.Nickname);
        int canonical;

        if (!string.IsNullOrEmpty(nickname)
            && _nicknameToCanonical.TryGetValue(nickname, out var existing))
        {
            // Same nickname already canonical under a different entity_id —
            // alias the incoming id to it.
            canonical = existing;
        }
        else
        {
            // First sighting of this nickname (or no nickname): incoming id
            // becomes its own canonical.
            canonical = incoming;
            if (!string.IsNullOrEmpty(nickname))
                _nicknameToCanonical[nickname] = canonical;
        }
        _aliases[incoming] = canonical;

        // Merge update into the canonical entry. No-downgrade rules for cp /
        // server / job preserve already-known values when a later packet emits
        // 0 (truncated/partial broadcast) or a transient lower CP value
        // (op=0297's CP field flickers for non-host members — wire-confirmed
        // 2026-05-02). IsSelf is sticky once set.
        int cp = info.CombatPower;
        int server = info.Server;
        int job = info.Job;
        bool isSelf = info.IsSelf;

        if (_entries.TryGetValue(canonical, out var prev))
        {
            if (cp == 0 || cp < prev.CombatPower) cp = prev.CombatPower;
            // Treat <= 0 as "not present" to defend against handlers that
            // return -1 instead of 0 for missing fields. Without this, a
            // later packet with server=-1 overwrites a previously valid
            // server (1021 etc.), dropping the [server] suffix.
            if (server <= 0) server = prev.Server;
            if (job <= 0) job = prev.Job;
            isSelf = isSelf || prev.IsSelf;
        }
        _entries[canonical] = new NicknameEntry(nickname ?? string.Empty, isSelf, server, job, cp);

        if (isSelf)
        {
            SelfUserId = canonical;
            if (!string.IsNullOrWhiteSpace(nickname)) SelfNickname = nickname;
            PinSelf(canonical, nickname);
            // SELF_NICK's "server" field carries a region/account code, not
            // the lobby shard id we need for the suffix. Don't assign
            // SelfServerId from the IsSelf path — fall through to the
            // nickname-match branch below, which picks up the real shard id
            // from op=0297 records.
        }
        else if (SelfNickname is string selfName
                 && string.Equals(selfName, nickname, StringComparison.Ordinal))
        {
            // Nickname-inferred self path: when SelfNickname was set via
            // RecordSelfNickCandidates BEFORE this canonical existed,
            // SelfUserId may still be null. Adopt this canonical now so
            // EvictStaleMembers / UI / etc. can identify self consistently.
            SelfUserId ??= canonical;
            if (server > 0) SelfServerId = server;
            PinSelf(canonical, nickname);
        }

        return canonical;
    }

    /// <summary>Pin self's canonical entry + nickname so the bounded dicts
    /// never evict them under FIFO pressure. Called every time we identify
    /// (or re-identify) self — Pin is idempotent so repeated calls are free,
    /// and we always pin the latest known canonical/nickname even if either
    /// changes mid-session.</summary>
    private void PinSelf(int canonical, string? nickname)
    {
        _entries.Pin(canonical);
        _aliases.Pin(canonical);
        if (!string.IsNullOrEmpty(nickname))
            _nicknameToCanonical.Pin(nickname);
    }

    /// <summary>
    /// Single normalization point for nicknames entering the registry. Returns
    /// NFC form (composed Hangul) so all dictionary lookups hit the same key
    /// regardless of which handler delivered the string.
    /// </summary>
    private static string? NormalizeNickname(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        return raw.IsNormalized(System.Text.NormalizationForm.FormC)
            ? raw
            : raw.Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>
    /// Update CombatPower for the canonical entry whose nickname matches.
    /// Used by the standalone op=0x00 0x92 CP broadcast (nickname-keyed, no
    /// entity_id). Returns 1 if patched, 0 otherwise. No-downgrade rule.
    /// </summary>
    public int UpdateCombatPowerByName(string nickname, int serverId, int combatPower)
    {
        if (string.IsNullOrWhiteSpace(nickname) || combatPower <= 0) return 0;
        var key = NormalizeNickname(nickname);
        if (string.IsNullOrEmpty(key)) return 0;
        if (!_nicknameToCanonical.TryGetValue(key, out var canonical)) return 0;
        if (!_entries.TryGetValue(canonical, out var entry)) return 0;
        if (serverId > 0 && entry.Server > 0 && entry.Server != serverId) return 0;
        if (combatPower <= entry.CombatPower) return 0;
        int newServer = entry.Server > 0 ? entry.Server : serverId;
        _entries[canonical] = new NicknameEntry(
            entry.Nickname, entry.IsSelf, newServer, entry.Job, combatPower);
        return 1;
    }

    /// <summary>
    /// Update the SelfNickname inference table from a Strong party-roster
    /// broadcast. Each call increments a frequency counter for every nickname
    /// in the broadcast; once a single nickname has appeared materially more
    /// often than any other, we adopt it as <see cref="SelfNickname"/>.
    /// Solo broadcasts (only one member) heavily boost that single name —
    /// being alone in a matchmaking room is itself a strong self-signal.
    /// </summary>
    public void RecordSelfNickCandidates(IEnumerable<string> nicknames, int groupId, bool soloBoost)
    {
        if (SelfNickname != null) return;  // already known via SELF_NICK or earlier inference

        int weight = soloBoost ? 5 : 1;
        string? winner = null;

        foreach (var rawNick in nicknames)
        {
            if (string.IsNullOrWhiteSpace(rawNick)) continue;
            // Same NFC normalization as Register so multi-room overlap detection
            // hashes to the same key regardless of which packet handler delivered
            // the nick. Without this, 짭호 in two different rosters could land in
            // two different dictionary entries → set.Count never reaches 2 →
            // winner never fires → SelfUserId stays null.
            var nick = NormalizeNickname(rawNick)!;

            _selfNickCandidates.TryGetValue(nick, out int prev);
            _selfNickCandidates[nick] = prev + weight;

            if (groupId != 0)
            {
                if (!_nickGroupIds.TryGetValue(nick, out var set))
                {
                    set = new HashSet<int>();
                    _nickGroupIds[nick] = set;
                }
                set.Add(groupId);
                if (winner is null && set.Count >= 2) winner = nick;
            }
        }

        if (winner is null)
        {
            string? leader = null;
            int leaderScore = 0;
            int runnerUp = 0;
            foreach (var (nick, score) in _selfNickCandidates)
            {
                if (score > leaderScore) { runnerUp = leaderScore; leader = nick; leaderScore = score; }
                else if (score > runnerUp) runnerUp = score;
            }
            if (leader != null && leaderScore >= 3 && leaderScore - runnerUp >= 3)
                winner = leader;
        }

        if (winner != null)
        {
            SelfNickname = winner;
            // Also adopt SelfUserId from the inferred nickname's canonical
            // so downstream code (e.g., EvictStaleMembers' "never evict self"
            // gate) recognizes the user without requiring a SELF_NICK packet
            // — when the meter starts mid-session, SELF_NICK has already
            // fired and won't fire again until logout/login, so the
            // multi-room nickname inference is the only self-id signal we
            // get. Without this, the user's own row gets evicted from
            // _partyMembers during sparse-broadcast windows (private room,
            // host idle for >60s) and "내 닉네임은 안뜨는데?" reproduces.
            if (_nicknameToCanonical.TryGetValue(winner, out var canonicalForSelf))
            {
                SelfUserId = canonicalForSelf;
                PinSelf(canonicalForSelf, winner);
            }
            foreach (var kv in _entries)
            {
                if (kv.Value.Server > 0
                    && string.Equals(kv.Value.Nickname, winner, StringComparison.Ordinal))
                {
                    SelfServerId = kv.Value.Server;
                    break;
                }
            }
        }
    }

    public string? GetName(int userId) => GetEntry(userId)?.Nickname;

    public NicknameEntry? GetEntry(int userId)
    {
        if (!_aliases.TryGetValue(userId, out var canonical)) return null;
        return _entries.TryGetValue(canonical, out var e) ? e : null;
    }

    /// <summary>
    /// Reverse lookup: nickname → canonical entity_id. Used by SUMMON_SPAWN
    /// (owner_name) to map a summon back to its owner's canonical id so
    /// damage credits the canonical Player row.
    /// </summary>
    public int? FindUserIdByName(string nickname)
    {
        var key = NormalizeNickname(nickname);
        if (string.IsNullOrEmpty(key)) return null;
        return _nicknameToCanonical.TryGetValue(key, out var c) ? c : null;
    }

    /// <summary>
    /// Returns true if the given nickname has been observed across multiple
    /// matchmaking rooms in this session — a strong signal that this nick is
    /// the user's own character (other players don't move between rooms during
    /// a session). Used by the cold-start kick-detection path in DpsAggregator
    /// when SelfUserId hasn't been confirmed yet.
    /// </summary>
    public bool IsMultiRoomCandidate(string? nickname)
    {
        var key = NormalizeNickname(nickname);
        if (string.IsNullOrEmpty(key)) return false;
        return _nickGroupIds.TryGetValue(key, out var set) && set.Count >= 2;
    }

    /// <summary>Enumerates canonical entries (one per nickname).</summary>
    public IEnumerable<KeyValuePair<int, NicknameEntry>> All => _entries;
}

public sealed record NicknameEntry(string Nickname, bool IsSelf, int Server, int Job, int CombatPower);
