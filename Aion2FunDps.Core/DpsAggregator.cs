using Aion2FunDps.Core.Models;
using Aion2FunDps.Core.Repositories;
using Aion2FunDps.Core.Sessions;

namespace Aion2FunDps.Core;

/// <summary>
/// Consumes IGameEvents and maintains current Session + per-player stats + nickname registry.
/// Re-attributes summon damage to its owner via SummonRepository.
/// Cross-thread: capture thread mutates state via OnEvent; UI thread reads via
/// OurCrew / ResolvePrimary / Registry queries and may mutate via
/// EvictStaleMembers (called from the 500ms refresh tick). All such methods
/// take <see cref="_stateLock"/>; without it, plain Dictionary/HashSet
/// enumeration on the UI side can throw "Collection was modified" or skip
/// entries mid-Refresh, which surfaces as "joiner appeared in roster log but
/// not in meter" until a later event reshuffles state.
///
/// Canonical-id model: every party member has ONE canonical entity_id (the
/// first one we saw via SELF_NICK / op=0297 / OTHER_NICK / op=01 92 etc.) and
/// all subsequent id-space transitions (lobby ↔ dungeon ↔ raid bulk) alias to
/// the same canonical. PlayerStats / _partyMembers / _roomTracker all key off
/// canonical, so a single Player has exactly one row, one entry in the party
/// set, and one slot in the room tracker — eliminating the entire class of
/// bugs caused by id-space drift (duplicate "나" + 짭호 rows, CP appearing on
/// some rows but not others, stale dungeon ids surviving room changes).
/// </summary>
public sealed class DpsAggregator
{
    /// <summary>
    /// Serializes capture-thread mutations against UI-thread reads. Held by
    /// OnEvent (capture), OurCrew / ResolvePrimary / EvictStaleMembers (UI).
    /// Critical sections are short (one packet's worth of state updates, or
    /// one snapshot copy) so contention is negligible at typical game packet
    /// rates (≤2k events/s).
    /// </summary>
    private readonly object _stateLock = new();

    private readonly NicknameRegistry _registry;
    private readonly SummonRepository _summons;
    private readonly EntityRegistry _entities;
    private readonly BossTracker _boss;
    private readonly AccuracyEstimator _accuracy;
    /// <summary>
    /// Canonical entity_ids of confirmed party members. Populated by
    /// nickname-bearing events (SELF_NICK / op=0297 / op=01 92) and by the
    /// cold-start damage-on-boss heuristic. Canonical-only by construction
    /// (RegisterCanonical resolves through aliases before adding).
    /// </summary>
    private readonly HashSet<int> _partyMembers = new();
    /// <summary>
    /// State machine that reconciles strong (op=0297, op=6ae2) and weak
    /// (op=0197) roster snapshots into a single trusted roster. The aggregator
    /// delegates room-change / add / confirmed-remove decisions here instead
    /// of trying to apply each snapshot directly. Tracker also operates on
    /// canonical ids — aggregator translates raw broadcast ids → canonical
    /// before passing them in.
    /// </summary>
    private readonly RoomLifecycleTracker _roomTracker = new();
    public RoomLifecycleTracker RoomTracker => _roomTracker;

    /// <summary>
    /// Optional dungeon-id → name lookup. When set, <see cref="CurrentDungeonName"/>
    /// reflects the latest dungeon announcement (op=0297) so the UI can mirror
    /// the in-game lobby header. App.xaml.cs wires this up alongside the mob
    /// and skill databases.
    /// </summary>
    public DungeonDatabase? DungeonDb { get; set; }
    public int CurrentDungeonId { get; private set; }
    public string? CurrentDungeonName { get; private set; }

    public Session Current { get; private set; }
    public NicknameRegistry Registry => _registry;
    public SummonRepository Summons => _summons;
    public EntityRegistry Entities => _entities;
    public BossTracker Boss => _boss;
    public AccuracyEstimator Accuracy => _accuracy;
    public string? RosterDebugLogPath { get; set; }

    public long DamageEventCount { get; private set; }
    public long DotEventCount { get; private set; }
    public long ReattributedDamageCount { get; private set; }
    public long HpEventCount { get; private set; }
    public long NicknameEventCount { get; private set; }
    public long CombatBoundaryEventCount { get; private set; }
    public long SummonSpawnEventCount { get; private set; }

    private bool _autoResetOnBoss = true;
    public bool AutoResetOnBoss
    {
        get => _autoResetOnBoss;
        set
        {
            if (_autoResetOnBoss == value) return;
            _autoResetOnBoss = value;

            // Toggling OFF mid-session leaves the freeze that OnBossKilled
            // installed on every player's _frozenDps pinned forever:
            // Apply() deliberately preserves the freeze across trailing
            // hits (DoTs/multi-hits), and the natural cleanup —
            // ResetCore on the next NewBossDetected — is gated by
            // AutoResetOnBoss=true. Without an explicit unfreeze here the
            // user sees "딜 누적이 안 됨" — actually the stats are
            // accumulating, but the Dps getter still returns the kill-
            // moment frozen value. Clear it explicitly when the user
            // opts out of the freeze contract.
            //
            // We also clear LastKilledBossId — it was stamped by the
            // last OnBossKilled and used by phase-transition guards in
            // OnNewBossDetected; in cumulative mode those guards are
            // moot (the function returns early on AutoResetOnBoss=false
            // anyway), but a stale LastKilledBossId would interfere
            // with PartyLeft handling at line ~1032 if the user toggles
            // back to ON later.
            if (!value)
            {
                foreach (var p in Current.AllPlayers)
                    p.UnfreezeAllDps();
                LastKilledBossId = null;
            }
        }
    }

    /// <summary>Set briefly when an auto-reset just fired — UI can flash a notification.</summary>
    public DateTime? LastAutoResetAt { get; private set; }

    /// <summary>
    /// Wall-clock timestamp of the most recent boss kill. Used as a
    /// fallback signal for phase-transition suppression (각성전 / 1인 컨텐츠
    /// where each phase has DIFFERENT mob_codes so the same-mob_code B5 guard
    /// can't catch them). When a new boss-grade entity appears within
    /// <see cref="PhaseTransitionWindow"/> of a previous kill we treat it as
    /// the next phase of the same fight, not a new encounter (audit 2026-05-04
    /// 각성전 case: 3 phases → 3 ResetCore in 5min wiped accumulated DPS).
    /// </summary>
    private DateTime? _lastBossKilledAt;
    // Tuning: 60s comfortably covers 각성전 phase-transition cinematics
    // (kill → spawn-spawn → cinematic → first damage observed at 26s in
    // the 2026-05-04 wire trace) without erroneously merging genuinely
    // separate fights. After a clean win the user typically takes >60s
    // before engaging the next pull anyway.
    private static readonly TimeSpan PhaseTransitionWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Entity-id of the most recently killed boss-grade entity. Set by
    /// <see cref="OnBossKilled"/>, cleared by <see cref="ResetCore"/> (which
    /// runs on NewBossDetected → next-boss-engage). The UI consults this to
    /// keep the damage column pinned to the killed boss until the user
    /// engages a new pull — preventing the "leaderboard resets after final
    /// boss" symptom in dungeons where the boss spans multiple entity_ids
    /// (나트하라 phase entities live as separate ids alongside the main
    /// player-targeted entity).
    /// </summary>
    public int? LastKilledBossId { get; private set; }

    /// <summary>
    /// Most recent mob_code seen via <see cref="EncounterAnnouncement"/>. Used to
    /// assign a name to the next boss-grade entity that appears (since the announce
    /// packet doesn't carry entityId itself, but is broadcast around encounter start).
    /// </summary>
    private int? _latestEncounterMobCode;

    /// <summary>
    /// Recent encounter announces (mob_code → timestamp). Used to recover from
    /// the race where two announces fire in quick succession before any damage
    /// lands (entering a room with two pre-aggro'd bosses) — the second one
    /// would otherwise overwrite <see cref="_latestEncounterMobCode"/>, leaving
    /// the first boss's entityId never linked to its mob_code (wrong banner
    /// name, missing kill credit). On the first damage to an unknown-mob_code
    /// target we walk this queue and pick the most recent within 5 seconds.
    /// Capped at 4 entries — multi-boss rooms cap out around 3.
    /// (audit 2026-05-04: B10 medium.)
    /// </summary>
    private readonly LinkedList<(int MobCode, DateTime At)> _recentAnnounces = new();
    private static readonly TimeSpan AnnounceMatchWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Matchmaking room id (op=02 97 groupId) the user is currently in. The
    /// authoritative gate for every roster decision: same-room vs new-room vs
    /// lobby-browse is decided by comparing the incoming roster's roomId
    /// against this. Cleared on PartyLeft (op=1D 97) and on kick detection
    /// (same-room Strong without self in members).
    ///
    /// Why this exists separately from RoomLifecycleTracker._currentRoomId:
    /// the tracker computes RoomChanged based on its own internal state, but
    /// we need our own canonical "what room am I in" so the four membership
    /// cases (same/new × self-in/self-out) can be handled in one place,
    /// without three components having to stay in lockstep.
    /// </summary>
    private int? _currentMatchmakingRoom;

    /// <summary>
    /// Per-canonical "last time we saw this member in any broadcast" timestamp.
    /// Updated whenever a member appears in a Strong/Weak roster, gets a
    /// LiveStatus add (op=0B 97), or lands a damage event. <see cref="EvictStaleMembers"/>
    /// uses this to remove members who've stopped appearing entirely — the
    /// canonical signal for "joiner left the room" since the wire doesn't
    /// carry an explicit per-member leave packet.
    /// </summary>
    private readonly Dictionary<int, DateTime> _memberLastSeenUtc = new();
    /// <summary>
    /// How long a member must be absent from ALL broadcasts before <see
    /// cref="EvictStaleMembers"/> drops them. Backup signal — primary
    /// removal happens via Strong/Weak delta.Removed when a roster fires
    /// without the member.
    ///
    /// 60s tuning rationale: matchmaking-room broadcast cadence varies
    /// widely (8-man active rooms ~3-5s, 4-man rooms ~30-60s, idle
    /// private rooms 60s+). Lower thresholds (30s) false-evicted real
    /// members during the natural quiet windows in 4-man rooms — wire-
    /// confirmed 2026-05-02: room=1689618 had a 41s gap between Strongs
    /// where all 4 members were stable; 30s eviction wiped the 3
    /// non-self members mid-window, then the next Strong re-added them,
    /// producing the "다 안맞고 이상해진다" symptom.
    /// 60s comfortably exceeds typical 4-man broadcast cadence while
    /// still cleaning up genuinely-stale phantoms within a minute. Self
    /// is exempt via the SelfUserId gate regardless.
    /// </summary>
    private static readonly TimeSpan StaleMemberThreshold = TimeSpan.FromSeconds(60);

    private void TouchMember(int canonicalId)
    {
        if (canonicalId <= 0) return;
        _memberLastSeenUtc[canonicalId] = DateTime.UtcNow;
    }

    /// <summary>
    /// Diagnostic: tracks the timestamp each canonical was added via
    /// LiveStatus (op=0B 97 PartyAccept). Used to classify each LiveStatus
    /// add as CONFIRMED (the canonical later appears in a Strong roster's
    /// newSet → real joiner) or PHANTOM (no Strong includes them within
    /// 5 seconds → likely false-positive from non-our-room 0B 97 broadcast,
    /// or extremely fast leave). Pure diagnostic — does NOT affect roster
    /// behavior. Reads logged to RosterDebugLogPath for offline analysis.
    /// </summary>
    private readonly Dictionary<int, DateTime> _liveStatusPendingConfirm = new();
    private static readonly TimeSpan LiveStatusPhantomThreshold = TimeSpan.FromSeconds(5);

    private void NoteLiveStatusAdd(int canonical, string nickname)
    {
        _liveStatusPendingConfirm[canonical] = DateTime.UtcNow;
        if (RosterDebugLogPath == null) return;
        try
        {
            System.IO.File.AppendAllText(RosterDebugLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} → LIVESTATUS_ADD       canonical={canonical} nick={nickname}\n");
        }
        catch { }
    }

    private void NoteStrongConfirm(IReadOnlyList<int> memberCanonicalIds)
    {
        if (_liveStatusPendingConfirm.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var c in memberCanonicalIds)
        {
            if (!_liveStatusPendingConfirm.TryGetValue(c, out var addedAt)) continue;
            var elapsed = (now - addedAt).TotalSeconds;
            _liveStatusPendingConfirm.Remove(c);
            if (RosterDebugLogPath == null) continue;
            try
            {
                System.IO.File.AppendAllText(RosterDebugLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} → LIVESTATUS_CONFIRMED canonical={c} gap={elapsed:F2}s (real joiner)\n");
            }
            catch { }
        }
    }

    private void SweepLiveStatusPhantoms()
    {
        if (_liveStatusPendingConfirm.Count == 0) return;
        var now = DateTime.UtcNow;
        List<int>? expired = null;
        foreach (var (id, addedAt) in _liveStatusPendingConfirm)
        {
            if (now - addedAt > LiveStatusPhantomThreshold)
                (expired ??= new List<int>()).Add(id);
        }
        if (expired == null) return;
        foreach (var id in expired)
        {
            var addedAt = _liveStatusPendingConfirm[id];
            var elapsed = (now - addedAt).TotalSeconds;
            _liveStatusPendingConfirm.Remove(id);
            if (RosterDebugLogPath == null) continue;
            try
            {
                System.IO.File.AppendAllText(RosterDebugLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} → LIVESTATUS_PHANTOM   canonical={id} elapsed={elapsed:F2}s (no Strong newSet contained this id)\n");
            }
            catch { }
        }
    }

    /// <summary>
    /// Drops _partyMembers entries that haven't appeared in any broadcast
    /// within <see cref="StaleMemberThreshold"/>. Caller (UI tick) invokes
    /// this periodically.
    ///
    /// Scope: ONLY runs when the user is not in any matchmaking room
    /// (_currentMatchmakingRoom == null). The new 4-case state machine
    /// (UPDATE / KICKED / ROOM_CHANGE / BROWSE_IGNORE) handles every member
    /// add/remove via REPLACE semantics on every roster broadcast, so the
    /// "joiner left silently" backup eviction is no longer needed inside
    /// a known room.
    ///
    /// Why the room gate matters: while in a matchmaking room, the user
    /// can be in the LOBBY (rosters fire freely) or in the DUNGEON the
    /// room launches into (rosters pause; only damage events touch
    /// lastSeen). In the dungeon case, OTHER_NICK enrichment for distant
    /// members can be sparse — their dungeon entity_ids might not yet
    /// alias to their lobby canonical, so damage events touch a different
    /// canonical and the lobby canonical's lastSeen goes stale within 60s.
    /// Eviction would then strip every other member mid-dungeon ("성역
    /// 방에서 갑자기 다 사라짐", 사용자 보고 2026-05-03 02:38).
    ///
    /// Without the room gate, fixing this would require IsBossMode gating
    /// during *every* dungeon transition (between bosses, lobby returns
    /// after wipes, etc.) which is fragile. Gating on "in any room" makes
    /// the rule trivial: rooms manage their own membership via the state
    /// machine, eviction only cleans up between rooms.
    /// </summary>
    public void EvictStaleMembers()
    {
        lock (_stateLock)
        {
        // Diagnostic phantom sweep runs unconditionally — it only logs and
        // doesn't mutate roster state.
        SweepLiveStatusPhantoms();

        // In any matchmaking room (lobby or dungeon-from-room): the state
        // machine owns membership. Don't second-guess it with time-based
        // eviction.
        if (_currentMatchmakingRoom.HasValue) return;
        if (_memberLastSeenUtc.Count == 0) return;

        // Self is in the party by definition — broadcasts for self can be
        // sparse (private room, host idle, mid-session meter start where
        // SELF_NICK has already fired) but the user is always there.
        int? selfId = _registry.SelfUserId;

        var now = DateTime.UtcNow;
        List<int>? toEvict = null;
        foreach (var (id, lastSeen) in _memberLastSeenUtc)
        {
            if (id == selfId) continue;
            if (!_partyMembers.Contains(id))
            {
                (toEvict ??= new List<int>()).Add(id);
                continue;
            }
            if (now - lastSeen > StaleMemberThreshold)
                (toEvict ??= new List<int>()).Add(id);
        }
        if (toEvict == null) return;

        foreach (var id in toEvict)
        {
            _partyMembers.Remove(id);
            Current.Remove(id);
            _memberLastSeenUtc.Remove(id);
            // Sync the room tracker too — without this, _roomTracker._roster
            // keeps the evicted id, and a later Weak/Strong containing that
            // id finds it already in _roster, returns delta.Added=[], and
            // the aggregator never re-adds them to _partyMembers. Member
            // disappears permanently from the leaderboard until manual reset.
            _roomTracker.RemoveMember(id);
        }
        }
    }

    public DpsAggregator()
    {
        _registry = new NicknameRegistry();
        _summons = new SummonRepository();
        _entities = new EntityRegistry();
        _boss = new BossTracker();
        _accuracy = new AccuracyEstimator();
        Current = new Session();
        _boss.NewBossDetected += OnNewBossDetected;
        _boss.BossKilled += OnBossKilled;
        _boss.BossReset += OnBossReset;
    }

    /// <summary>
    /// Registers a NicknameInfo and reconciles any orphan PlayerStats that
    /// existed under the raw entity_id (e.g., cold-start damage applied
    /// before the nickname was learned). Returns the canonical entity_id
    /// for downstream membership / stats lookups.
    ///
    /// If the incoming id was aliased to a DIFFERENT canonical (the same
    /// nickname is already known under another id), any orphan PlayerStats
    /// keyed on the incoming raw id is removed — canonical is the single
    /// authoritative row going forward. _partyMembers is also re-keyed.
    /// </summary>
    private int RegisterCanonical(NicknameInfo nick)
    {
        int incoming = nick.UserId;
        int canonical = _registry.Register(nick);
        if (canonical != incoming)
        {
            // Merge orphan stats (cold-start damage that landed on the raw
            // entity_id before nickname registration) into the canonical
            // row. Earlier code DELETED the orphan, losing tank/opener burst
            // accumulated in the seconds before OTHER_NICK arrived (audit
            // 2026-05-04: B2 high — DPS undercount visible on leaderboard).
            var orphan = Current.GetExisting(incoming);
            if (orphan != null)
            {
                var canonStats = Current.GetOrCreate(canonical);
                canonStats.MergeFrom(orphan);
                Current.Remove(incoming);
            }
            if (_partyMembers.Remove(incoming))
                _partyMembers.Add(canonical);
            _memberLastSeenUtc.Remove(incoming);
        }
        TouchMember(canonical);
        return canonical;
    }

    private void OnBossKilled(int bossId)
    {
        // Freeze only makes sense in auto-reset mode where each boss kill is a
        // self-contained fight whose final stats users want to inspect post-kill.
        // In cumulative mode (AutoResetOnBoss = false) users want continuous
        // multi-boss tracking — freezing here would leave the DPS pinned at the
        // first kill while TotalDamage keeps growing, which is the inconsistent
        // state users reported as confusing.
        if (!AutoResetOnBoss) return;

        // Snapshot every player's current rolling DPS so the displayed number stays
        // pinned to the kill moment until the next boss takes damage. Reset is
        // deferred to NewBossDetected (= first hit on the next boss), so the user
        // controls how long they view kill stats by when they engage the next pull.
        foreach (var p in Current.AllPlayers)
        {
            p.FreezeDps();
            p.FreezeDpsToTarget(bossId);
        }

        // Pin the damage column to this boss until the next pull. Without
        // this, BossTracker.UpdateFocus shifts focus to whatever boss-grade
        // entity is still "alive" in _entities (server reused jobbed entity
        // slots → 나트하라 phase entities) and the leaderboard's damage
        // numbers swap to those entities' tiny per-id totals.
        LastKilledBossId = bossId;
        _lastBossKilledAt = DateTime.UtcNow;
    }

    private void OnNewBossDetected(int bossId)
    {
        // Link this boss entityId to the most recently announced encounter mob_code.
        // The announce packet (0x01 0x91) typically arrives just before the boss's
        // first MOB_HP packet; if it didn't (multi-pull/stale encounter), no link.
        // Queue fallback handles the "two announces fire before any damage" race
        // (multi-boss room) — pick the most recent announce within the match
        // window so both entityIds get linked correctly.
        var now = DateTime.UtcNow;
        int? linked = _latestEncounterMobCode;
        if (!linked.HasValue && _recentAnnounces.Count > 0)
        {
            for (var node = _recentAnnounces.Last; node != null; node = node.Previous)
            {
                if (now - node.Value.At <= AnnounceMatchWindow)
                {
                    linked = node.Value.MobCode;
                    break;
                }
            }
        }
        if (linked is { } mc)
            _entities.Register(bossId, mc);

        if (!AutoResetOnBoss)
        {
            LogResetDecision(bossId, "AutoResetOff", reset: false);
            return;
        }

        // Mid-encounter guard: if another boss-grade entity is still alive, we are in
        // the middle of a multi-boss fight (waves, adds, linked bosses). Don't ResetCore
        // — that would wipe the active boss's accumulated data. CRITICAL: do NOT
        // bump LastAutoResetAt here. Earlier code stamped it on every skipped fire,
        // which made the "보스 감지 — 자동 리셋" flash banner blink for every add
        // spawn during multi-boss phases. Flash should fire ONLY when an actual
        // reset happens.
        if (_boss.HasOtherAliveBoss(bossId))
        {
            LogResetDecision(bossId, "OtherAliveBoss", reset: false);
            return;
        }

        // Multi-id phase-boss suppression (e.g., 나트하라). The previous phase
        // entity went HP→0, OnBossKilled stamped LastKilledBossId. Then the
        // server spawns the next phase as a new entity_id sharing the same
        // mob_code. ResetCore here would clear LastKilledBossId AND wipe the
        // frozen DPS rows, so the user's hard-earned phase-1 numbers vanish
        // mid-fight. Treat this as a continuation, not a new encounter
        // (audit 2026-05-04: B5 high — wire-confirmed pattern from kill-display
        // memory project_dungeons_bosses_mapping).
        if (LastKilledBossId is int killedId
            && _entities.GetMobCode(killedId) is int killedMob
            && _entities.GetMobCode(bossId) is int newMob
            && killedMob == newMob)
        {
            LogResetDecision(bossId, "SameMobCodePhaseTransition", reset: false);
            return;
        }

        // (Removed "RecentKillPhaseTransition" 60s window — based on wrong
        // premise that 각성전 was multi-phase. Actually each 각성전 difficulty
        // level is a separate fight, so reset between them is correct.)

        // First hit on a NEW boss (different entity, no other boss alive):
        // full reset so the next fight starts clean.
        var snapshot = _boss.GetEntity(bossId);

        ResetCore();
        LastAutoResetAt = DateTime.UtcNow;
        LogResetDecision(bossId, "NewBossEngaged", reset: true);

        if (snapshot != null)
            _boss.RestoreEntity(bossId, snapshot.MaxHp, snapshot.CurrentHp);

        // Reset survives ResetCore — re-register the link so it persists into the new session.
        if (_latestEncounterMobCode is { } mc2)
            _entities.Register(bossId, mc2);
    }

    private void LogResetDecision(int bossId, string reason, bool reset)
    {
        if (string.IsNullOrEmpty(_boss.DiagnosticLogPath)) return;
        try
        {
            System.IO.File.AppendAllText(_boss.DiagnosticLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} {(reset ? "→ RESET_FIRED   " : "→ RESET_SKIPPED ")} mobId={bossId} reason={reason}\n");
        }
        catch { }
    }

    private DateTime _lastDamageLogAt = DateTime.MinValue;
    private void LogDamageApplied(DamageEvent dmg, int actor, long preDmg, long postDmg)
    {
        if (string.IsNullOrEmpty(_boss.DiagnosticLogPath)) return;
        if (!_boss.FocusedEntityId.HasValue || dmg.TargetId != _boss.FocusedEntityId.Value) return;
        var now = DateTime.UtcNow;
        if (now - _lastDamageLogAt < TimeSpan.FromMilliseconds(200)) return;
        _lastDamageLogAt = now;
        try
        {
            bool inParty = _partyMembers.Contains(actor);
            bool inRegistry = _registry.GetEntry(actor) != null;
            System.IO.File.AppendAllText(_boss.DiagnosticLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} → DMG_APPLIED      raw={dmg.ActorId} canon={actor} target={dmg.TargetId} dmg={dmg.Damage} pre={preDmg} post={postDmg} inParty={(inParty ? "T" : "F")} inRegistry={(inRegistry ? "T" : "F")}\n");
        }
        catch { }
    }

    /// <summary>
    /// Boss entityId waiting for the user's next damage hit to trigger a
    /// reset. Set by <see cref="OnBossReset"/> when wipe is detected so the
    /// previous attempt's leaderboard stays on screen until the user
    /// re-engages.
    /// </summary>
    private int? _pendingWipeResetBossId;

    private void OnBossReset(int bossId)
    {
        if (!AutoResetOnBoss) return;
        _pendingWipeResetBossId = bossId;
        LastAutoResetAt = DateTime.UtcNow;
    }

    private void LinkFocusedBossToEncounter(int mobCode)
    {
        if (!_boss.FocusedEntityId.HasValue) return;
        if (!_boss.IsBossMode) return;

        int bossId = _boss.FocusedEntityId.Value;
        if (_entities.GetMobCode(bossId).HasValue) return;

        _entities.Register(bossId, mobCode);
    }

    /// <summary>
    /// Pulls leak indicators from upstream subsystems and recomputes confidence.
    /// Call from UI refresh tick (e.g., once per second).
    /// </summary>
    public void RefreshAccuracy(long droppedPackets, long malformedFrames, long unknownOpcodes)
    {
        _accuracy.DroppedPackets = droppedPackets;
        _accuracy.MalformedFrames = malformedFrames;
        _accuracy.UnknownOpcodes = unknownOpcodes;

        bool driftMeasurable = false;
        if (_boss.FocusedEntityId.HasValue)
        {
            int focusId = _boss.FocusedEntityId.Value;
            long maxHp = _boss.FocusedMaxHp ?? 0;
            long curHp = _boss.FocusedCurrentHp ?? 0;
            long hpDelta = maxHp >= curHp ? maxHp - curHp : 0;

            long damageToFocus = 0;
            foreach (var p in Current.AllPlayers)
            {
                if (p.DamagePerTarget.TryGetValue(focusId, out var d))
                    damageToFocus += d;
            }

            if (hpDelta > 0 && damageToFocus > 0)
            {
                long leak = Math.Max(0, hpDelta - damageToFocus);
                _accuracy.HpDamageDrift = (double)leak / hpDelta;
                driftMeasurable = true;
            }
            else
            {
                _accuracy.HpDamageDrift = 0;
            }
        }
        _accuracy.HasDriftSignal = driftMeasurable;

        _accuracy.Recompute();
    }

    public void OnEvent(IGameEvent evt)
    {
        lock (_stateLock)
        {
        switch (evt)
        {
            case DamageEvent dmg:
                {
                    int rawActor = dmg.ActorId;
                    int effectiveActor = _summons.GetOwner(rawActor) ?? rawActor;
                    if (!IsPlayerDamage(rawActor, effectiveActor, dmg.TargetId, dmg.SkillCode))
                        break;

                    // Resolve to canonical Player. Falls back to effectiveActor
                    // as self-canonical when not yet registered (cold-start
                    // damage before nickname is learned). RegisterCanonical
                    // later collapses the orphan into the canonical row when
                    // the nickname arrives.
                    int actor = _registry.ResolveCanonical(effectiveActor) ?? effectiveActor;

                    // Encounter-announce fallback: link target entityId to
                    // the most recent announced mob_code so BossTracker can
                    // recognize it as boss without waiting for HP threshold.
                    if (_latestEncounterMobCode is { } pendingMc
                        && _entities.GetMobCode(dmg.TargetId) == null)
                    {
                        _entities.Register(dmg.TargetId, pendingMc);
                        _latestEncounterMobCode = null;
                    }

                    // Pending wipe-reset: BossReset already fired (HP back to
                    // full) but we held the reset off until this hit lands.
                    if (AutoResetOnBoss
                        && _pendingWipeResetBossId is int pendingWipeId
                        && dmg.TargetId == pendingWipeId)
                    {
                        var wipeSnap = _boss.GetEntity(pendingWipeId);
                        ResetCore();
                        LastAutoResetAt = DateTime.UtcNow;
                        if (wipeSnap != null)
                            _boss.RestoreEntity(pendingWipeId, wipeSnap.MaxHp, wipeSnap.CurrentHp);
                        var wipeMc = _entities.GetMobCode(pendingWipeId);
                        if (wipeMc.HasValue) _entities.Register(pendingWipeId, wipeMc.Value);
                        _pendingWipeResetBossId = null;
                        LogResetDecision(pendingWipeId, "WipeResume", reset: true);
                    }

                    // BossTracker first so any auto-reset triggered by this damage
                    // (NewBossDetected → ResetCore) happens BEFORE we apply the hit
                    // to Current.
                    _boss.OnDamage(dmg);

                    if (actor != rawActor) ReattributedDamageCount++;
                    var stats = Current.GetOrCreate(actor);
                    long preDmg = stats.TotalDamage;
                    stats.Apply(dmg);
                    if (dmg.IsDot) DotEventCount++;
                    else DamageEventCount++;
                    LogDamageApplied(dmg, actor, preDmg, stats.TotalDamage);
                    TouchMember(actor);

                    // Cold-start party detection: any actor landing damage on
                    // a confirmed boss-grade entity is a party member —
                    // BUT only add to _partyMembers when we already have a
                    // nickname-resolved canonical for them. Without the
                    // GetEntry gate, a distant member's dungeon entity_id
                    // (not yet aliased to their lobby canonical via
                    // OTHER_NICK) gets added under the raw id, producing a
                    // duplicate row alongside the lobby canonical entry the
                    // matchmaking roster already added (audit 2026-05-04:
                    // B12 medium). Once OTHER_NICK arrives, RegisterCanonical's
                    // orphan-merge folds the damage onto the canonical row.
                    if (_boss.IsBossMode
                        && _boss.FocusedEntityId == dmg.TargetId
                        && !_partyMembers.Contains(actor)
                        && _registry.GetEntry(actor) != null)
                    {
                        _partyMembers.Add(actor);
                        _roomTracker.AddLiveMember(actor);
                    }
                }
                break;

            case PartyRosterUpdate roster:
                {
                    // Self inference: feed every roster into the registry's
                    // frequency table BEFORE registering members, so the
                    // nickname-match path inside Register already knows who
                    // self is by the time it runs. Multi-room overlap is the
                    // strongest signal — only the user appears in multiple
                    // matchmaking rooms over a session.
                    if (roster.Confidence == RosterConfidence.Strong
                        && roster.GroupId != 0
                        && roster.Members.Count > 0)
                    {
                        bool solo = roster.Members.Count == 1;
                        _registry.RecordSelfNickCandidates(
                            roster.Members.Select(m => m.Nickname),
                            roster.GroupId,
                            soloBoost: solo);
                    }

                    // Register every member → canonical entity_ids. Always
                    // runs (even for non-our-room rosters), so OTHER_NICK-
                    // style enrichment for distant players still happens.
                    var memberCanonicalIds = new int[roster.Members.Count];
                    for (int i = 0; i < roster.Members.Count; i++)
                        memberCanonicalIds[i] = RegisterCanonical(roster.Members[i]);

                    int? selfId = _registry.SelfUserId;
                    bool selfInRoster = selfId.HasValue
                        && Array.IndexOf(memberCanonicalIds, selfId.Value) >= 0;
                    bool selfKnown = selfId.HasValue;
                    int rosterRoom = roster.GroupId;
                    int? currentRoom = _currentMatchmakingRoom;

                    // 4-case state machine (matches A2Viewer's PartyTracker
                    // semantics, generalized to roomId-aware decisions):
                    //
                    //   (current, self in)   → SAME ROOM UPDATE
                    //                          REPLACE _partyMembers = members.
                    //                          Anyone missing from this roster
                    //                          (kicked / left) is dropped.
                    //
                    //   (current, self out)  → SELF KICKED / LEFT
                    //                          Treat like PartyLeft: wipe.
                    //                          The server is telling us we're
                    //                          no longer in the room.
                    //
                    //   (different, self in) → MOVED TO NEW ROOM
                    //                          Wipe old, set new room, REPLACE
                    //                          with members.
                    //
                    //   (different, self out)→ LOBBY BROWSE OF OTHER ROOM
                    //                          IGNORE entirely. Names already
                    //                          registered above for damage
                    //                          attribution; membership state
                    //                          stays untouched.
                    //
                    // Cold start (selfKnown=false): we can't classify rooms
                    // yet. Don't pollute _partyMembers — wait for SelfNickname
                    // / SelfUserId to be inferred via RecordSelfNickCandidates
                    // (multi-room overlap). Once known, the next roster bootstraps
                    // membership via the (different, self in) path.
                    //
                    // Why this replaces the old Strong/Weak ADD-only-vs-replace
                    // distinction: Aion 2 KR sends "lobby preview" packets
                    // (multi-room, browse list) in the same opcode family
                    // (op=01 97) as our actual party state. Treating any of
                    // these as "ADD members" without a roomId/self gate caused
                    // members from random other rooms to leak into our meter
                    // ("어디서 왔는지 모를 사람들이 미터기에 떠있음"). The new
                    // model: every roster decision turns on (sameRoom, selfIn).

                    if (!selfKnown)
                    {
                        // Cold start: SelfUserId not yet identified (no SELF_NICK
                        // mid-session, no multi-room overlap, no solo Strong yet).
                        //
                        // Strong (op=02 97) fires only for the user's own room —
                        // verified against A2Viewer source 2026-05-03. Trust it
                        // unconditionally and REPLACE _partyMembers from it.
                        //
                        // Weak (op=01 97) carries lobby-browser previews of OTHER
                        // rooms in cold-start risk — without SelfUserId we can't
                        // pick the right block. BUT once a Strong has set
                        // _currentMatchmakingRoom, a Weak whose groupId matches
                        // that room is unambiguously about our room (the
                        // dispatcher already pinned it via _currentLobbyRoomId).
                        // Trust those for kick / joiner-leave detection — without
                        // it, getting kicked in cold-start leaves the kicker's
                        // room rendered in the meter forever (사용자 보고
                        // 2026-05-04: "원정 대기방에서 강퇴당했는데 안없어짐").
                        //
                        // Cold-start REPLACE doesn't have a "self exemption" —
                        // we don't know who self is — so a kick that drops the
                        // user from members will drop them from _partyMembers
                        // too. That's correct: a kicked user is no longer in the
                        // room. The Strong of their next room replaces state
                        // again via the COLDSTART_BOOT path.
                        bool coldNewRoom = !currentRoom.HasValue || currentRoom.Value != rosterRoom;

                        bool coldTrust;
                        string coldLabel;
                        if (roster.Confidence == RosterConfidence.Strong)
                        {
                            coldTrust = true;
                            coldLabel = coldNewRoom ? "COLDSTART_BOOT" : "COLDSTART_UPD";
                        }
                        else if (currentRoom.HasValue && rosterRoom == currentRoom.Value)
                        {
                            // Weak for our pinned current room — trust as update.
                            coldTrust = true;
                            coldLabel = "COLDSTART_WEAK_UPD";
                        }
                        else
                        {
                            // Weak for some other room (lobby browse) — ignore.
                            coldTrust = false;
                            coldLabel = "COLDSTART_WEAK";
                        }

                        LogRosterTransition(roster, coldLabel);

                        if (!coldTrust)
                        {
                            NicknameEventCount += roster.Members.Count;
                            break;
                        }

                        if (coldNewRoom)
                        {
                            WipeMembership();
                            _currentMatchmakingRoom = rosterRoom;
                        }

                        // Cold-start kick detection: if the only id missing from
                        // the incoming roster has been observed across multiple
                        // matchmaking rooms in this session, it's almost
                        // certainly self (other players don't move between
                        // rooms). Treat as KICKED — wipe everything, clear
                        // _currentMatchmakingRoom — instead of REPLACE which
                        // would only remove self and leave the kicker's room
                        // members rendered in the meter forever (사용자 보고
                        // 2026-05-04: "원정 대기방 들어갔다가 나왔는데 사람들
                        // 안없어짐"). Only fires when SAME room (not on initial
                        // BOOT where _partyMembers was empty anyway).
                        if (!coldNewRoom && _partyMembers.Count > 0)
                        {
                            var coldNewSetCheck = new HashSet<int>(memberCanonicalIds);
                            var dropped = _partyMembers.Where(id => !coldNewSetCheck.Contains(id)).ToList();
                            if (dropped.Count == 1)
                            {
                                var droppedNick = _registry.GetName(dropped[0]);
                                if (_registry.IsMultiRoomCandidate(droppedNick))
                                {
                                    LogRosterTransition(roster, "COLDSTART_KICKED");
                                    WipeMembership();
                                    _currentMatchmakingRoom = null;
                                    NicknameEventCount += roster.Members.Count;
                                    break;
                                }
                            }
                        }

                        var coldNewSet = new HashSet<int>(memberCanonicalIds);
                        foreach (var id in _partyMembers.ToList())
                        {
                            if (coldNewSet.Contains(id)) continue;
                            _partyMembers.Remove(id);
                            Current.Remove(id);
                            _memberLastSeenUtc.Remove(id);
                            _roomTracker.RemoveMember(id);
                        }
                        foreach (var canonicalId in memberCanonicalIds)
                        {
                            _partyMembers.Add(canonicalId);
                            Current.GetOrCreate(canonicalId);
                            TouchMember(canonicalId);
                        }
                        NicknameEventCount += roster.Members.Count;
                        break;
                    }

                    bool sameRoom = currentRoom.HasValue && rosterRoom != 0 && currentRoom.Value == rosterRoom;
                    bool newRoom = !currentRoom.HasValue || (rosterRoom != 0 && currentRoom.Value != rosterRoom);

                    if (sameRoom && !selfInRoster)
                    {
                        // CASE: kicked from current room.
                        LogRosterTransition(roster, "KICKED");
                        WipeMembership();
                        _currentMatchmakingRoom = null;
                        NicknameEventCount += roster.Members.Count;
                        break;
                    }

                    if (newRoom && !selfInRoster)
                    {
                        // CASE: lobby-browse preview of another room.
                        LogRosterTransition(roster, "BROWSE_IGNORE");
                        NicknameEventCount += roster.Members.Count;
                        break;
                    }

                    // selfInRoster = true from here on.

                    if (newRoom && roster.Confidence != RosterConfidence.Strong)
                    {
                        // CASE: stale Weak roster post-PartyLeft. Aion 2 KR
                        // sometimes delivers a 01 97 lobby-preview packet a
                        // few hundred ms after a 1D 97 PartyLeft — same
                        // window where the user creates a new room. Without
                        // this gate, the stale Weak's old roster (containing
                        // already-departed joiners) re-enters the meter via
                        // the ROOM_CHANGE path and there's no follow-up
                        // packet to clean it up. Wire-confirmed 2026-05-03
                        // 02:31:41: PARTY_LEFT at .586, stale Weak at .694
                        // with last room's [self, 나가블루, 이븐핑]. A2Viewer
                        // handles this with a _justLeft one-shot suppression
                        // flag (PartyStreamParser.cs case 1). We use a
                        // simpler invariant: only Strong (op=02 97) can
                        // bootstrap a new room — Weak previews are always
                        // for an already-known room.
                        LogRosterTransition(roster, "WEAK_BOOTSTRAP_IGNORE");
                        NicknameEventCount += roster.Members.Count;
                        break;
                    }

                    if (newRoom)
                    {
                        // CASE: moved into a new room. Wipe old, set new.
                        LogRosterTransition(roster, "ROOM_CHANGE");
                        WipeMembership();
                        _currentMatchmakingRoom = rosterRoom;
                    }
                    else
                    {
                        // CASE: same room, self present. Update.
                        LogRosterTransition(roster, "UPDATE");
                    }

                    // REPLACE _partyMembers with newSet, with one exemption:
                    // damage-bearing members from a SAME-ROOM update are
                    // preserved if missing from this packet. Aion 2 sometimes
                    // emits partial broadcasts (subset of room) — not common
                    // but observed. Wiping a damage-bearing member to a stale
                    // partial would lose their kill stats. The exemption only
                    // applies when newSet is partial (LooksPartialUpdate-like
                    // signal: any incoming member missing CP / job suggests a
                    // partial), modeled after A2Viewer's UNION fallback.
                    var incomingComplete = roster.Members.Count > 0
                        && roster.Members.All(m => m.Server > 0 && m.CombatPower > 0);
                    var newSet = new HashSet<int>(memberCanonicalIds);

                    var toRemove = new List<int>();
                    foreach (var id in _partyMembers)
                    {
                        if (newSet.Contains(id)) continue;
                        if (id == selfId) continue;  // self always preserved
                        if (!incomingComplete)
                        {
                            // Partial broadcast: keep damage-bearing rows so
                            // kill stats don't get wiped by a 4-of-6 update.
                            var prev = Current.GetExisting(id);
                            if (prev != null && prev.TotalDamage > 0) continue;
                        }
                        toRemove.Add(id);
                    }
                    foreach (var id in toRemove)
                    {
                        _partyMembers.Remove(id);
                        Current.Remove(id);
                        _memberLastSeenUtc.Remove(id);
                        _roomTracker.RemoveMember(id);
                    }

                    foreach (var canonicalId in memberCanonicalIds)
                    {
                        _partyMembers.Add(canonicalId);
                        Current.GetOrCreate(canonicalId);
                        TouchMember(canonicalId);
                    }

                    NicknameEventCount += roster.Members.Count;
                }
                break;

            case NicknameInfo nick:
                {
                    int canonical = RegisterCanonical(nick);
                    NicknameEventCount++;
                    if (nick.IsSelf || nick.IsPartyMember)
                    {
                        bool wasKnown = _partyMembers.Contains(canonical);
                        bool trackerAdded = _roomTracker.AddLiveMember(canonical);
                        _partyMembers.Add(canonical);
                        Current.GetOrCreate(canonical);
                        if (!wasKnown || trackerAdded)
                            LogLivePartyMember(nick);
                        // Diagnostic: track LiveStatus adds for confirm/phantom
                        // classification. Self adds always come from
                        // RecordSelfNickCandidates / SELF_NICK and aren't from
                        // op=0B 97, so skip. PartyMember adds in this code path
                        // come exclusively from PartyMemberStatusHandler with
                        // isPartyAccept=true (op=0B 97).
                        if (nick.IsPartyMember && !nick.IsSelf)
                            NoteLiveStatusAdd(canonical, nick.Nickname);
                    }
                }
                break;

            case PartyLeft:
                {
                    // op=1d97 fires for "you left the matchmaking room". Wire-
                    // confirmed 2026-05-03 00:31~00:33: every 1d97 in this
                    // session was a real room transition — not the stable-room
                    // false-firing I previously suspected. Re-enabled wipe so
                    // 사용자 perception "방 나가면 리셋이 안 됨" is fixed.
                    //
                    // Discriminator (kept from before): if a boss kill just
                    // happened (LastKilledBossId set), preserve so the user
                    // can inspect the kill record after dungeon exit.
                    CurrentDungeonId = 0;
                    CurrentDungeonName = null;
                    if (RosterDebugLogPath != null)
                    {
                        try
                        {
                            string lastKilled = LastKilledBossId?.ToString() ?? "null";
                            string partyBefore = string.Join(",", _partyMembers);
                            System.IO.File.AppendAllText(RosterDebugLogPath,
                                $"{DateTime.Now:HH:mm:ss.fff} PARTY_LEFT received (LastKilledBossId={lastKilled}, partyBefore=[{partyBefore}])\n");
                        }
                        catch { }
                    }
                    // Decouple membership wipe from room-id clear:
                    //   - membership wipe is gated by LastKilledBossId so the
                    //     post-kill leaderboard stays visible after dungeon exit
                    //   - _currentMatchmakingRoom is ALWAYS cleared so
                    //     EvictStaleMembers can do its job and so the next
                    //     Strong roster takes the COLDSTART_BOOT/ROOM_CHANGE
                    //     path correctly (audit 2026-05-04: B11 medium —
                    //     previously the room id stayed pinned forever after a
                    //     successful clear, blocking new-room detection until
                    //     the next Strong arrived).
                    //
                    // AutoResetOnBoss=false addition: in cumulative mode the
                    // user explicitly chose "no per-boss reset", so OnBossKilled
                    // returns early and LastKilledBossId never gets stamped.
                    // Without this guard the PartyLeft path would interpret
                    // that absence as "no kill happened, safe to wipe", which
                    // wiped the user's accumulated dungeon totals on dungeon
                    // exit — directly contradicting the cumulative mode's
                    // intent. Skip the wipe in that mode too; the next
                    // ROOM_CHANGE (entering a new matchmaking room) is the
                    // natural reset trigger that matches user expectation
                    // (사용자 보고 2026-05-04: 던전 끝나도 누적 유지, 다음
                    // 원정방 입장 시 리셋).
                    if (AutoResetOnBoss && !LastKilledBossId.HasValue)
                        WipeMembership();
                    _currentMatchmakingRoom = null;
                }
                break;

            case DungeonAnnouncement dungeon:
                if (dungeon.DungeonId != CurrentDungeonId)
                {
                    CurrentDungeonId = dungeon.DungeonId;
                    CurrentDungeonName = DungeonDb?.GetName(dungeon.DungeonId);
                }
                break;

            case CombatPowerUpdate cpu:
                // Standalone CP broadcast (op=0x00 0x92). Nickname-keyed —
                // updates the canonical entry directly.
                _registry.UpdateCombatPowerByName(cpu.Nickname, cpu.ServerId, cpu.CombatPower);
                break;

            case CombatBoundary:
                CombatBoundaryEventCount++;
                break;

            case MobHpUpdate hp:
                _boss.OnHpUpdate(hp);
                HpEventCount++;
                break;

            case SummonSpawnInfo sp:
                {
                    // Owner resolution priority:
                    //   1) OwnerName → canonical (most authoritative when present)
                    //   2) OwnerId  → canonical alias (works even before nickname
                    //      arrives — earlier code ignored OwnerId entirely, so
                    //      pet damage from Bard/Cleric/Sorc summons silently
                    //      vanished until the owner's OTHER_NICK arrived;
                    //      audit 2026-05-04: B3 high)
                    //   3) Fall back to raw actor_id at damage time (existing).
                    int? owner = null;
                    if (!string.IsNullOrEmpty(sp.OwnerName))
                        owner = _registry.FindUserIdByName(sp.OwnerName);
                    if (owner is null && sp.OwnerId != 0)
                        owner = _registry.ResolveCanonical(sp.OwnerId) ?? sp.OwnerId;
                    if (owner.HasValue)
                        _summons.Register(sp.SummonId, owner.Value);
                    if (sp.MobCode.HasValue)
                        _entities.Register(sp.SummonId, sp.MobCode.Value);
                    SummonSpawnEventCount++;
                }
                break;

            case EncounterAnnouncement enc:
                _latestEncounterMobCode = enc.MobCode;
                // Append to recent queue (cap 4) so the second-of-two-announces
                // case (multi-boss room) doesn't lose the first announce.
                _recentAnnounces.AddLast((enc.MobCode, DateTime.UtcNow));
                while (_recentAnnounces.Count > 4) _recentAnnounces.RemoveFirst();
                LinkFocusedBossToEncounter(enc.MobCode);
                break;
        }
        }
    }

    /// <summary>
    /// Wipes all membership state — _partyMembers, the per-id PlayerStats
    /// rows in the current Session, last-seen timestamps, and the
    /// RoomLifecycleTracker's roster. Called by the kick / leave / room-
    /// change paths in the roster state machine and by PartyLeft. Does NOT
    /// touch _currentMatchmakingRoom — caller decides whether to clear it
    /// (kick / PartyLeft) or set it to a new id (room change).
    /// </summary>
    private void WipeMembership()
    {
        foreach (var oldId in _partyMembers.ToList())
            Current.Remove(oldId);
        _partyMembers.Clear();
        _memberLastSeenUtc.Clear();
        _roomTracker.Clear();
    }

    /// <summary>
    /// Single-line diagnostic for every roster decision (KICKED / BROWSE_IGNORE
    /// / ROOM_CHANGE / UPDATE). Together with the existing LogRosterUpdate
    /// dump, this lets us correlate "the meter showed wrong members" reports
    /// against the exact transition the state machine took.
    /// </summary>
    private void LogRosterTransition(PartyRosterUpdate roster, string transition)
    {
        if (RosterDebugLogPath == null) return;
        try
        {
            string members = string.Join(",",
                roster.Members.Select(m => $"{m.UserId}:{m.Nickname}"));
            string party = string.Join(",", _partyMembers);
            System.IO.File.AppendAllText(RosterDebugLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} {transition,-13} room={roster.GroupId} ({roster.Confidence}) cur={_currentMatchmakingRoom?.ToString() ?? "null"} self={_registry.SelfUserId?.ToString() ?? "null"} members=[{members}] partyBefore=[{party}]\n");
        }
        catch { }
    }

    private void LogRosterUpdate(PartyRosterUpdate roster, RoomLifecycleTracker.SnapshotDelta delta)
    {
        if (RosterDebugLogPath == null) return;

        try
        {
            string members = string.Join(",",
                roster.Members.Select(m => $"{m.UserId}:{m.Nickname}(s={m.Server},cp={m.CombatPower})"));
            string added = string.Join(",", delta.Added);
            string removed = string.Join(",", delta.Removed);
            string party = string.Join(",", _partyMembers);

            System.IO.File.AppendAllText(RosterDebugLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} {roster.Confidence} room={roster.GroupId} self={roster.ContainsSelf} " +
                $"selfNick={_registry.SelfNickname ?? "?"} selfSrv={_registry.SelfServerId?.ToString() ?? "?"} " +
                $"members={roster.Members.Count} [{members}] changed={delta.RoomChanged} add=[{added}] remove=[{removed}] partyBefore=[{party}]\n");
        }
        catch { }
    }

    private void LogLivePartyMember(NicknameInfo nick)
    {
        if (RosterDebugLogPath == null) return;

        try
        {
            string party = string.Join(",", _partyMembers);
            System.IO.File.AppendAllText(RosterDebugLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} LiveStatus add={nick.UserId}:{nick.Nickname} partyAfter=[{party}]\n");
        }
        catch { }
    }

    private bool IsPlayerDamage(int rawActorId, int effectiveActorId, int targetId, uint skillCode)
    {
        // Target check FIRST: if target is an ally (registered player or self),
        // this is a heal / buff / shield / friendly tick — NOT offensive damage.
        bool targetIsRegistered = _registry.GetEntry(targetId) != null;
        bool targetIsKnownMob = _entities.GetMobCode(targetId).HasValue;
        if ((targetIsRegistered && !targetIsKnownMob) || _registry.SelfUserId == targetId)
            return false;

        // Target is enemy. Now classify the actor.

        // If raw actor has a known mob_code, it's a non-player entity — pet,
        // summon, or boss/mob. Count its damage when SummonRepository maps
        // it back to ANY owner id (registered nickname not required —
        // RegisterCanonical's orphan-merge pass folds the damage onto the
        // owner's canonical row once the OTHER_NICK arrives, so requiring
        // the registry entry up-front silently dropped pet damage during
        // the cold-start window; audit 2026-05-04: B3/H5 medium).
        if (_entities.GetMobCode(rawActorId).HasValue)
        {
            return _summons.GetOwner(rawActorId).HasValue;
        }

        if (_registry.GetEntry(effectiveActorId) != null)
            return true;

        if (_summons.IsSummon(rawActorId) && _registry.GetEntry(effectiveActorId) != null)
            return true;

        // Self often has no OTHER_NICK entry. Until SELF_NICK arrives, class-coded
        // skills are the best signal that the actor is a player rather than a mob.
        return JobClassDetector.FromSkillCode(skillCode) != JobClass.Unknown;
    }

    /// <summary>
    /// Returns players whose stats look "real" — registered via NicknameInfo, OR have
    /// enough damage/hits to not be incoming-damage noise from mobs.
    /// </summary>
    public IEnumerable<PlayerStats> LikelyPlayers(long minDamage = 50_000, int minHits = 10)
    {
        foreach (var p in Current.AllPlayers)
        {
            bool isRegistered = _registry.GetEntry(p.ActorId) != null;
            bool meetsThreshold = p.TotalDamage >= minDamage || p.HitCount >= minHits;
            if (isRegistered || meetsThreshold)
                yield return p;
        }
    }

    /// <summary>
    /// Returns the canonical PlayerStats rows representing our party.
    /// Canonical-by-construction — no UI dedupe needed.
    /// </summary>
    public IEnumerable<PlayerStats> OurCrew()
    {
        // Snapshot under lock, then return a concrete list. OnEvent runs on
        // the capture thread; raw enumeration of HashSet/Dictionary while it
        // mutates can throw "Collection was modified" mid-Refresh, silently
        // truncating the leaderboard for that tick (사용자 보고: "방 만들고
        // 두번째 사람 들어왔는데 안 뜸" — joiner appeared in roster log but
        // never in meter, race-on-LiveStatus-add hypothesis). A snapshot is
        // cheap (party ≤ 8 in matchmaking, AllPlayers usually < 20).
        lock (_stateLock)
        {
            if (_partyMembers.Count > 0)
            {
                var memberSet = new HashSet<int>(_partyMembers);
                var result = new List<PlayerStats>(memberSet.Count);
                foreach (var p in Current.AllPlayers)
                {
                    if (_summons.IsSummon(p.ActorId)) continue;
                    if (memberSet.Contains(p.ActorId))
                        result.Add(p);
                }
                return result;
            }

            // Fallback: no confirmed party yet — primary heuristic.
            var primary = ResolvePrimaryUnlocked();
            return primary == null ? Array.Empty<PlayerStats>() : new[] { primary };
        }
    }

    /// <summary>
    /// Atomic snapshot of boss + reset state for the UI's per-tick render.
    /// Without this, MainViewModel.Refresh reads FocusedEntityId, IsBossMode,
    /// GetEntity(focusId), and LastKilledBossId across separate calls — the
    /// capture thread can fire OnBossKilled → ResetCore between any two reads,
    /// producing a stale focusId whose entity was just removed (NullReferenceException
    /// at entity.MaxHp). This single locked read keeps the four fields coherent.
    /// </summary>
    public sealed record BossSnapshot(
        int? FocusedEntityId,
        bool IsBossMode,
        long FocusedCurrentHp,
        long FocusedMaxHp,
        int? FocusedMobCode,
        int? LastKilledBossId);

    public BossSnapshot SnapshotBoss()
    {
        lock (_stateLock)
        {
            int? focus = _boss.FocusedEntityId;
            bool isMode = _boss.IsBossMode;
            long curHp = 0, maxHp = 0;
            int? mobCode = null;
            if (focus.HasValue)
            {
                var entity = _boss.GetEntity(focus.Value);
                if (entity != null)
                {
                    curHp = entity.CurrentHp;
                    maxHp = entity.MaxHp;
                }
                else
                {
                    // Focus id was set but entity already evicted — treat as no-focus
                    // for this tick instead of returning a stale id the UI would
                    // null-deref on.
                    focus = null;
                    isMode = false;
                }
                if (focus.HasValue)
                    mobCode = _entities.GetMobCode(focus.Value);
            }
            return new BossSnapshot(focus, isMode, curHp, maxHp, mobCode, LastKilledBossId);
        }
    }

    public IEnumerable<PlayerStats> BossDamageDealers(int bossId)
    {
        foreach (var p in Current.AllPlayers)
        {
            if (p.GetDamageToTarget(bossId) <= 0) continue;
            if (_registry.GetEntry(p.ActorId) != null || p.LooksLikePlayer)
                yield return p;
        }
    }

    public PlayerStats? ResolvePrimary()
    {
        lock (_stateLock) return ResolvePrimaryUnlocked();
    }

    private PlayerStats? ResolvePrimaryUnlocked()
    {
        // 1. Registered self (definitive — SELF_NICK packet was received)
        if (_registry.SelfUserId.HasValue
            && Current.AllPlayers.FirstOrDefault(p => p.ActorId == _registry.SelfUserId.Value) is { } selfP)
            return selfP;

        // 2. Single-unnamed-actor heuristic.
        var unnamed = Current.AllPlayers
            .Where(p => _registry.GetName(p.ActorId) == null && p.LooksLikePlayer)
            .ToList();
        if (unnamed.Count == 1)
            return unnamed[0];

        return null;
    }

    /// <summary>
    /// Ends current session and starts a fresh one. Resets boss focus + per-entity HP tracking.
    /// Preserves NicknameRegistry (names/aliases stay) and SummonRepository (summon mappings stay).
    /// </summary>
    public void Reset() => ResetCore();

    /// <summary>
    /// Manual reset triggered by the user (Ctrl+R / ↻ button) — also clears the
    /// party member set so a stale roster from the previous matchmaking room
    /// doesn't linger when the user joins a new room.
    /// </summary>
    public void ResetForNewRoom()
    {
        // Diagnostic: this is the ONLY code path that wipes _partyMembers
        // without leaving a transition log entry (because it's a user action,
        // not a wire event). Log before-state so future bug reports of "members
        // disappeared without explanation" can be attributed (or ruled out)
        // against accidental ↻ click / Ctrl+R press in chat.
        if (RosterDebugLogPath != null)
        {
            try
            {
                string partyBefore = string.Join(",", _partyMembers);
                System.IO.File.AppendAllText(RosterDebugLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} MANUAL_RESET    cur={_currentMatchmakingRoom?.ToString() ?? "null"} partyBefore=[{partyBefore}]\n");
            }
            catch { }
        }

        _partyMembers.Clear();
        _memberLastSeenUtc.Clear();
        _roomTracker.Clear();
        ResetCore();
    }

    private void ResetCore()
    {
        // Diagnostic: log every ResetCore invocation with caller context so we
        // can trace mid-fight damage wipes. Log line 2026-05-04: pre values in
        // DMG_APPLIED keep going to 0 within a single boss fight even though
        // only ONE NEW_BOSS_FIRED + RESET_FIRED is logged. Some path is
        // calling ResetCore without LogResetDecision. This stack trace dump
        // (3 frames is enough to identify caller) catches it next reproduction.
        if (_boss.DiagnosticLogPath is string p)
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                var caller = st.FrameCount > 0 ? st.GetFrame(0)?.GetMethod()?.Name ?? "?" : "?";
                var caller2 = st.FrameCount > 1 ? st.GetFrame(1)?.GetMethod()?.Name ?? "?" : "?";
                System.IO.File.AppendAllText(p,
                    $"{DateTime.Now:HH:mm:ss.fff} → RESET_CORE       caller={caller}<-{caller2} partyMembers={_partyMembers.Count} totalDamageBefore={Current.AllPlayers.Sum(x => x.TotalDamage)}\n");
            }
            catch { }
        }
        Current.End();
        Current = new Session();
        _boss.Reset();
        LastKilledBossId = null;
        // Re-seed PlayerStats for canonical members so they keep their rows
        // through the reset (their NicknameRegistry entries / aliases persist;
        // PlayerStats were wiped by Current = new Session()).
        foreach (var memberId in _partyMembers)
            Current.GetOrCreate(memberId);

        DamageEventCount = 0;
        DotEventCount = 0;
        ReattributedDamageCount = 0;
        HpEventCount = 0;
        CombatBoundaryEventCount = 0;
        SummonSpawnEventCount = 0;

        _accuracy.RebaselineToCurrent();
    }
}
