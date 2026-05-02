namespace Aion2FunDps.Core.Sessions;

/// <summary>
/// Reconciles unreliable party-roster snapshots into a single trusted
/// "current room + roster" state. Different opcodes carry different
/// reliability levels:
///
///   Strong  — op=0297 (matchmaking broadcast), op=6ae2 (zone-load bulk).
///             Each snapshot is authoritative for the room it names: adds
///             AND removes are applied immediately.
///
///   Weak    — op=0197 (multi-room lobby preview). The packet often contains
///             our room alongside adjacent lobby rooms, and the parser can
///             miss members in an otherwise-valid room block. Adds are still
///             applied immediately (better to show a real joiner than to wait),
///             but removes require <see cref="WeakRemovalConfirmationThreshold"/>
///             consecutive weak snapshots that all agree on the absence —
///             otherwise a single parser miss would silently kick a real
///             member.
///
/// Room-change detection: a strong snapshot with a different roomId from the
/// current one wipes the existing roster atomically before applying the new
/// one, so damage-bearing stragglers from the previous matchmaking room don't
/// leak into the next.
/// </summary>
public sealed class RoomLifecycleTracker
{
    public const int WeakRemovalConfirmationThreshold = 2;

    private readonly HashSet<int> _roster = new();
    private readonly Dictionary<int, int> _weakMissingCount = new();
    private readonly Dictionary<int, DateTime> _recentlyAddedUntilUtc = new();
    /// <summary>
    /// Members removed by a Strong snapshot, with a deadline before a Weak
    /// snapshot is allowed to re-add them. Without this, op=0197 (lobby
    /// preview) packets that lag behind the authoritative op=0297 leave by a
    /// few seconds will keep re-adding the leaver — visible to the user as
    /// "the person who left the room takes forever to disappear".
    /// </summary>
    private readonly Dictionary<int, DateTime> _recentlyRemovedUntilUtc = new();
    private static readonly TimeSpan WeakReaddSuppressionWindow = TimeSpan.FromSeconds(30);
    private int? _currentRoomId;

    public int? CurrentRoomId => _currentRoomId;
    public IReadOnlyCollection<int> Roster => _roster;

    public sealed record SnapshotDelta(
        bool RoomChanged,
        IReadOnlyList<int> Added,
        IReadOnlyList<int> Removed);

    public SnapshotDelta ApplyStrong(int roomId, IEnumerable<int> memberIds, int? selfId = null, bool allMembersComplete = false)
    {
        var newSet = new HashSet<int>(memberIds);

        // Empty Strong roster broadcasts come in two flavours, and we have
        // to ignore BOTH because they're not real "you left" signals:
        //
        // 1. Wipe-respawn signal — when the party wipes inside a dungeon,
        //    the server emits op=0297 with a NEW (dungeon-checkpoint) groupId
        //    and zero members, then resumes silence until lobby return.
        //    Wire-confirmed 2026-05-02: groupId 1464985 (lobby) → wipe →
        //    1 spurious broadcast room=124712128 members=0 → ... → 1464985
        //    again on lobby return after kill.
        //
        // 2. Standalone "room cleared" pings the lobby server occasionally
        //    sends in similar shapes.
        //
        // Treating either as authoritative wipes _roster (and downstream
        // _partyMembers / Current PlayerStats) — the user-visible symptom
        // was "전원 죽고나서 보스한테 갈때 미터기에 나만 보임" because the
        // dungeon roster got blown away mid-fight, leaving OurCrew yielding
        // nothing while bossEngaged stayed true → "나" placeholder only.
        //
        // op=1d97 PartyLeft is the explicit user-left signal — rely on that
        // for legit roster clearing instead of inferring from empty op=0297.
        if (newSet.Count == 0)
        {
            return new SnapshotDelta(false, Array.Empty<int>(), Array.Empty<int>());
        }

        bool roomChanged = false;
        if (roomId != 0 && _currentRoomId.HasValue && _currentRoomId.Value != roomId)
        {
            roomChanged = true;
        }

        // "Host status" detection. When the user is the room creator, the lobby
        // server keeps emitting op=0297 broadcasts that contain ONLY the host
        // (parsed=1, members=[self]) every few seconds. These are NOT roster
        // snapshots — joiners arrive over a separate channel (PartyMemberStatus
        // / op=0197 lobby preview) and the host's own op=0297 never includes
        // them. Treating each [self-only] Strong packet as authoritative wiped
        // every joiner the instant they were registered, which is exactly the
        // "방 만들고 다른사람 들어와도 안 보임" symptom 사용자 reported.
        bool isHostStatusOnly =
            !roomChanged
            && newSet.Count == 1
            && _roster.Count > 1
            && selfId.HasValue
            && newSet.Contains(selfId.Value)
            && _roster.Contains(selfId.Value);

        var added = new List<int>();
        var removed = new List<int>();
        var now = DateTime.UtcNow;

        if (roomChanged)
        {
            // Hard wipe — every previous member is gone for new-room purposes
            // even if they happen to share an entity id with someone in the
            // new roster. The subsequent add pass re-introduces overlap.
            foreach (var oldId in _roster) removed.Add(oldId);
            _roster.Clear();
            _weakMissingCount.Clear();
            _recentlyAddedUntilUtc.Clear();
            _recentlyRemovedUntilUtc.Clear();
        }
        else
        {
            // Distinguish "real shrunk roster" (kick / leave) from "partial
            // broadcast" (NCSoft sent a subset of the room) using member
            // info completeness — A2Power's heuristic. A Strong with every
            // member carrying full stats (server + CP) is the lobby server
            // saying "this is the room", and removals are authoritative —
            // exactly the signal a kick produces. A Strong missing those
            // fields is a partial host-status ping; fall back to the join
            // grace so recently-LiveStatus'd joiners aren't wiped by it.
            //
            // [self only] is a special case: it can be either a real "you
            // kicked everyone" or the periodic host-status broadcast. We
            // can't tell from a single packet, so keep the grace fallback
            // for it regardless of completeness.
            bool authoritative = allMembersComplete && !isHostStatusOnly;
            foreach (var oldId in _roster)
            {
                if (newSet.Contains(oldId)) continue;
                if (!authoritative && IsInJoinGrace(oldId, now)) continue;
                removed.Add(oldId);
            }
        }

        foreach (var id in removed)
        {
            _roster.Remove(id);
            _weakMissingCount.Remove(id);
            _recentlyAddedUntilUtc.Remove(id);
            // Block weak snapshots from re-adding this id until the
            // suppression window expires. A genuine re-join still works
            // because op=0297 (Strong) re-adds with an explicit suppression
            // clear below.
            _recentlyRemovedUntilUtc[id] = now + WeakReaddSuppressionWindow;
        }

        foreach (var id in newSet)
        {
            if (_roster.Add(id))
            {
                added.Add(id);
                _recentlyAddedUntilUtc[id] = now.AddSeconds(12);
            }
            _weakMissingCount.Remove(id);
            // Strong snapshot is authoritative — clear the weak-readd
            // suppression so a genuine re-join works immediately.
            _recentlyRemovedUntilUtc.Remove(id);
        }

        if (roomId != 0) _currentRoomId = roomId;

        return new SnapshotDelta(roomChanged, added, removed);
    }

    public bool AddLiveMember(int memberId)
    {
        if (memberId <= 0) return false;

        bool added = _roster.Add(memberId);
        _weakMissingCount.Remove(memberId);
        // Only arm grace on the FIRST add. NCSoft sends per-member LiveStatus
        // pings periodically while the member is in the room — if we reset
        // grace on every ping, the member's grace never expires and a
        // subsequent kick (which arrives as a shrunk Strong) is permanently
        // protected from being applied. With this gate, grace expires 12 sec
        // after first sighting and the next shrunk Strong correctly removes
        // a kicked member.
        if (added)
            _recentlyAddedUntilUtc[memberId] = DateTime.UtcNow.AddSeconds(12);
        return added;
    }

    public SnapshotDelta ApplyLobby(int roomId, IEnumerable<int> memberIds, bool canRemoveMissing)
    {
        if (roomId == 0)
            return ApplyStrong(roomId, memberIds);

        var newSet = new HashSet<int>(memberIds);
        bool roomChanged = _currentRoomId.HasValue && _currentRoomId.Value != roomId;

        var added = new List<int>();
        var removed = new List<int>();

        if (!_currentRoomId.HasValue || roomChanged)
        {
            foreach (var oldId in _roster) removed.Add(oldId);
            _roster.Clear();
            _weakMissingCount.Clear();
            _recentlyRemovedUntilUtc.Clear();

            foreach (var id in newSet)
                if (_roster.Add(id)) added.Add(id);

            _currentRoomId = newSet.Count == 0 ? null : roomId;
            return new SnapshotDelta(roomChanged, added, removed);
        }

        var nowL = DateTime.UtcNow;
        foreach (var id in newSet)
        {
            if (_recentlyRemovedUntilUtc.TryGetValue(id, out var deadline) && deadline > nowL)
                continue;  // suppressed re-add
            if (_roster.Add(id)) added.Add(id);
            _weakMissingCount.Remove(id);
            _recentlyRemovedUntilUtc.Remove(id);
        }

        if (canRemoveMissing)
            ConfirmMissingMembers(newSet, removed);

        if (newSet.Count == 0 && _roster.Count == 0)
            _currentRoomId = null;

        return new SnapshotDelta(false, added, removed);
    }

    public SnapshotDelta ApplyWeak(int roomId, IEnumerable<int> memberIds, bool canRemoveMissing)
    {
        // Weak snapshots only inform the room we already know about.
        // Adopting an arbitrary roomId from a noisy lobby preview would
        // give us roster data for the wrong room.
        if (!_currentRoomId.HasValue || roomId != _currentRoomId.Value)
        {
            return new SnapshotDelta(false, [], []);
        }

        var newSet = new HashSet<int>(memberIds);
        var now = DateTime.UtcNow;

        // Drop ids inside the strong-removal suppression window — they were
        // just removed by a Strong snapshot and a stale lobby-preview packet
        // is trying to bring them back. Clean up expired entries while we're here.
        var stillSuppressed = new HashSet<int>();
        var expired = new List<int>();
        foreach (var (id, deadline) in _recentlyRemovedUntilUtc)
        {
            if (deadline <= now) expired.Add(id);
            else stillSuppressed.Add(id);
        }
        foreach (var id in expired) _recentlyRemovedUntilUtc.Remove(id);

        var added = new List<int>();
        foreach (var id in newSet)
        {
            if (stillSuppressed.Contains(id)) continue;  // weak re-add suppressed
            if (_roster.Add(id))
            {
                added.Add(id);
                _recentlyAddedUntilUtc[id] = now.AddSeconds(12);
            }
            _weakMissingCount.Remove(id);
        }

        var removed = new List<int>();
        // Removal path is gated on canRemoveMissing: a weak snapshot without
        // self is structurally suspect (could be a partial parse of our room,
        // or a near-duplicate block from the same lobby preview), so treating
        // its missing members as departures has high false-positive risk.
        // When it's gated off, real members keep their existing missing-count
        // state — neither incremented nor reset — so confirmation across
        // genuine self-bearing snapshots still works.
        if (canRemoveMissing)
            ConfirmMissingMembers(newSet, removed);

        return new SnapshotDelta(false, added, removed);
    }

    private void ConfirmMissingMembers(HashSet<int> newSet, List<int> removed)
    {
        foreach (var id in _roster)
        {
            if (newSet.Contains(id)) continue;
            _weakMissingCount.TryGetValue(id, out int count);
            count++;
            if (count >= WeakRemovalConfirmationThreshold)
            {
                removed.Add(id);
            }
            else
            {
                _weakMissingCount[id] = count;
            }
        }

        var now = DateTime.UtcNow;
        foreach (var id in removed)
        {
            _roster.Remove(id);
            _weakMissingCount.Remove(id);
            _recentlyAddedUntilUtc.Remove(id);
            // Even weak-confirmed removal arms the readd suppression; otherwise
            // the next op=0197 burst (which often arrives in pairs) would
            // immediately re-add the member that the previous burst just
            // confirmed gone.
            _recentlyRemovedUntilUtc[id] = now + WeakReaddSuppressionWindow;
        }
    }

    private bool IsInJoinGrace(int id, DateTime now)
    {
        if (!_recentlyAddedUntilUtc.TryGetValue(id, out var until)) return false;
        if (now <= until) return true;
        _recentlyAddedUntilUtc.Remove(id);
        return false;
    }

    public void Clear()
    {
        _roster.Clear();
        _weakMissingCount.Clear();
        _recentlyAddedUntilUtc.Clear();
        _recentlyRemovedUntilUtc.Clear();
        _currentRoomId = null;
    }

    /// <summary>
    /// Drops a single member from the tracker's roster — used by the
    /// aggregator's time-based eviction path. Unlike Strong/Weak removal
    /// this does NOT arm the WeakReaddSuppressionWindow, so a fresh
    /// broadcast for the same member can re-add them immediately.
    /// </summary>
    public void RemoveMember(int memberId)
    {
        _roster.Remove(memberId);
        _weakMissingCount.Remove(memberId);
        _recentlyAddedUntilUtc.Remove(memberId);
    }
}
