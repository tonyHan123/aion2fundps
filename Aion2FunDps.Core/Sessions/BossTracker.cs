using System.Collections.Concurrent;
using System.IO;
using Aion2FunDps.Core.Models;

namespace Aion2FunDps.Core.Sessions;

/// <summary>
/// Tracks per-entity HP and damage event counts. Identifies current "focus" target
/// (entity receiving most damage) and flags boss mode when focus has high HP.
/// </summary>
public sealed class BossTracker
{
    /// <summary>
    /// Fallback HP threshold for entities whose mob_code we don't know (SUMMON_SPAWN
    /// missed). When mob_code is known, <see cref="IsKnownBoss"/> is consulted instead
    /// — that's authoritative regardless of HP. The HP fallback stays conservative
    /// (5M) so we don't false-positive trash mobs in higher-tier dungeons (정복-어려움
    /// /성역) where mid-tier mobs can exceed 1~2M HP.
    /// </summary>
    public long BossHpThreshold { get; set; } = 5_000_000;

    /// <summary>
    /// Same role as <see cref="BossHpThreshold"/> but for UI display / kill log
    /// eligibility. Used as fallback when mob_code is unknown.
    /// </summary>
    public long BossModeDisplayThreshold { get; set; } = 5_000_000;

    /// <summary>
    /// Optional authoritative boss-status predicate. Returns:
    ///   true  → mob_code is known AND mobs.json marks it boss=true → boss-grade (HP ignored)
    ///   false → mob_code is known AND mobs.json marks it boss=false → NOT boss-grade (HP ignored)
    ///   null  → mob_code unknown (SUMMON_SPAWN not yet received) → fall back to HP threshold
    /// Wired up in App.xaml.cs to consult EntityRegistry → MobDatabase. This is the
    /// primary boss-detection signal; HP threshold is a fallback for unknown entities.
    /// </summary>
    public Func<int, bool?>? IsKnownBoss { get; set; }

    /// <summary>
    /// Fired when a new entity is observed with HP at or above BossHpThreshold.
    /// Subscribers (e.g., DpsAggregator) can use this to auto-reset session on boss engagement.
    /// </summary>
    public event Action<int>? NewBossDetected;

    /// <summary>
    /// Fired when a boss-grade entity (MaxHp ≥ BossModeDisplayThreshold) transitions
    /// from CurrentHp &gt; 0 to CurrentHp == 0. DpsAggregator subscribes to freeze the
    /// per-player DPS at the kill moment so users see the final number instead of
    /// watching it decay while standing idle.
    /// </summary>
    public event Action<int>? BossKilled;

    /// <summary>
    /// Fired when a boss we've been damaging suddenly jumps back to (near) full HP —
    /// the canonical wipe signature in Aion 2 (party dies → server respawns the boss
    /// at full). DpsAggregator subscribes to reset the session so the next attempt
    /// starts with fresh DPS instead of compounding stale damage from the wiped pull.
    /// </summary>
    public event Action<int>? BossReset;

    /// <summary>
    /// When set, every HP update on a non-trivial entity is appended here so we can
    /// reconstruct reset firing offline. Goal: figure out whether NewBossDetected
    /// fires for boss 2 in the "boss 1 → boss 2" transition the user reported.
    /// </summary>
    public string? DiagnosticLogPath { get; set; }
    private readonly object _logLock = new();

    private void Log(string evtName, int mobId, long maxHp, long prevHp, long curHp, bool isNew, bool bossDetected)
    {
        if (DiagnosticLogPath is null) return;
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(DiagnosticLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} {evtName,-18} mobId={mobId} " +
                    $"hp={curHp,-10} prev={prevHp,-10} max={maxHp,-10} " +
                    $"isNew={(isNew ? "T" : "F")} bossDetected={(bossDetected ? "T" : "F")}\n");
            }
        }
        catch { }
    }

    // ConcurrentDictionary so the UI thread's `Boss.GetEntity(focusId)` and
    // `IsBossMode` reads can race the capture thread's OnHpUpdate / UpdateFocus
    // writes safely. The plain Dictionary that was here previously could
    // corrupt its bucket array under typical boss-fight HP-update rates
    // (audit 2026-05-04: HIGH severity — same class as NicknameRegistry).
    private readonly ConcurrentDictionary<int, EntityState> _entities = new();

    public int? FocusedEntityId { get; private set; }
    /// <summary>
    /// UI-facing "in a boss fight" flag. Strictly tied to a confirmed boss mob_code
    /// (mobs.json boss=true) — no HP fallback. The HP-fallback heuristic used elsewhere
    /// (TryFireBossDetected / HasOtherAliveBoss) caused PvP false-positives in town:
    /// player characters with 20M+ HP tripped the 5M threshold and the meter started
    /// flashing the boss banner + [me] placeholder.
    ///
    /// Trade-off: bosses whose SUMMON_SPAWN we missed (cold-start, e.g. 침식 1넴)
    /// stay un-flagged for UI purposes — no boss banner, no placeholder. Damage
    /// measurement / auto-reset / drift tracking are unaffected (those use
    /// IsBossGrade with HP fallback).
    /// </summary>
    public bool IsBossMode
    {
        get
        {
            if (!FocusedEntityId.HasValue) return false;
            if (!_entities.TryGetValue(FocusedEntityId.Value, out var s)) return false;
            if (s.CurrentHp == 0) return false;
            return IsKnownBoss?.Invoke(FocusedEntityId.Value) == true;
        }
    }

    public long? FocusedMaxHp =>
        FocusedEntityId.HasValue && _entities.TryGetValue(FocusedEntityId.Value, out var s)
            ? s.MaxHp : null;

    public long? FocusedCurrentHp =>
        FocusedEntityId.HasValue && _entities.TryGetValue(FocusedEntityId.Value, out var s)
            ? s.CurrentHp : null;

    public void OnHpUpdate(MobHpUpdate hp)
    {
        bool isNewEntity = !_entities.ContainsKey(hp.MobId);
        var s = GetOrCreate(hp.MobId);

        // Capture pre-update MaxHp so the wipe-detection condition below can
        // distinguish "HP returned to ~full" (real wipe) from "MaxHp grew due
        // to boss enrage / phase rescale" (the new MaxHp is just larger, not
        // a respawn). Without this snapshot, a boss whose MaxHp jumps from
        // 60M → 100M mid-fight reads as `prevHp < newMaxHp*0.9 && curHp >=
        // newMaxHp*0.95` and fires a spurious BossReset (audit 2026-05-04:
        // B9 medium).
        long prevMaxHp = s.MaxHp;
        long prevHp = s.CurrentHp;
        s.CurrentHp = hp.CurrentHp;
        if (hp.CurrentHp > s.MaxHp) s.MaxHp = hp.CurrentHp;
        s.LastUpdateUtc = DateTime.UtcNow;

        // Log either if HP-significant OR if mob_code lookup says it's a boss
        // (so 1단계 small-HP bosses still get traced).
        bool isBossByCode = IsKnownBoss?.Invoke(hp.MobId) == true;
        bool worthLogging = isBossByCode
            || s.MaxHp >= BossModeDisplayThreshold
            || hp.CurrentHp >= BossModeDisplayThreshold;
        if (worthLogging)
            Log("HP_UPDATE", hp.MobId, s.MaxHp, prevHp, hp.CurrentHp, isNewEntity, s.BossDetected);

        TryFireBossDetected(hp.MobId, s, prevHp);

        // Wipe detection: a boss-grade entity we've been damaging suddenly
        // jumps back to (near) full HP. In Aion 2 the server respawns the
        // boss at full when the party dies, so this is the canonical wipe
        // signature. Gated on IncomingDamageEvents > 0 (don't false-fire on
        // first sighting) and prevHp dropping below ~90% (rules out tiny
        // healing ticks). MaxHp == 0 guard avoids divide-by-zero on the
        // very first HP packet.
        //
        // Single-fire by construction: we zero IncomingDamageEvents below,
        // so subsequent server resends of the same HP=full packet won't
        // satisfy the IncomingDamageEvents > 0 condition until the user
        // re-engages and lands a hit (which re-arms the counter).
        // HP fields are long (signed 64-bit) so this arithmetic is safe
        // for any plausible game HP. Earlier `uint MaxHp` overflowed when
        // MaxHp > 47.7M (since 47.7M * 90 > uint32 max), causing the
        // wipe-detection condition to silently evaluate false on every
        // modern boss (HP 60M-100M+ in 환영의 회랑 / 무의 요람 / etc.).
        if (!isNewEntity
            && prevHp > 0
            && s.MaxHp > 0
            && prevMaxHp == s.MaxHp  // MaxHp didn't grow this packet (rules out enrage rescale)
            && IsBossGrade(hp.MobId, s)
            && s.IncomingDamageEvents > 0
            && prevHp < s.MaxHp * 90 / 100
            && hp.CurrentHp >= s.MaxHp * 95 / 100)
        {
            Log("→ WIPE_FIRED", hp.MobId, s.MaxHp, prevHp, hp.CurrentHp, isNewEntity, s.BossDetected);
            s.IncomingDamageEvents = 0;
            s.IncomingDamageTotal = 0;
            BossReset?.Invoke(hp.MobId);
            return;
        }

        // Boss kill: HP went from >0 to 0 on an entity we actually engaged. The
        // IncomingDamageEvents gate is critical — without it, every player/party
        // member death (their HP packet → 0) would fire BossKilled because Aion 2's
        // MOB_HP opcode (0x00 0x8d) carries player HP too at endgame HP scales.
        if (!isNewEntity && prevHp > 0 && hp.CurrentHp == 0
            && IsBossGrade(hp.MobId, s)
            && s.IncomingDamageEvents > 0)
        {
            Log("→ KILL_FIRED", hp.MobId, s.MaxHp, prevHp, hp.CurrentHp, isNewEntity, s.BossDetected);
            BossKilled?.Invoke(hp.MobId);
        }
    }

    /// <summary>
    /// Returns true if entity should be treated as boss-grade for kill/display purposes.
    /// Authoritative path: mob_code lookup via <see cref="IsKnownBoss"/>. Fallback when
    /// unknown: HP-based <see cref="BossModeDisplayThreshold"/>.
    /// </summary>
    private bool IsBossGrade(int mobId, EntityState s)
    {
        bool? known = IsKnownBoss?.Invoke(mobId);
        return known switch
        {
            true => true,
            false => false,
            null => s.MaxHp >= BossModeDisplayThreshold,
        };
    }

    /// <summary>
    /// Fires <see cref="NewBossDetected"/> when an entity is FIRST observed to be
    /// (a) above the boss-HP threshold AND (b) damaged by us at least once.
    /// Both conditions must hold simultaneously, so we call this from both
    /// <see cref="OnHpUpdate"/> and <see cref="OnDamage"/> — whichever event
    /// completes the second condition triggers the fire.
    ///
    /// Why the IncomingDamageEvents gate? Player characters in Aion 2 KR have HP
    /// at the same scale as bosses (10M+ at endgame), and player HP comes through
    /// the same MOB_HP opcode. Without this gate, every party member's HP
    /// observation would falsely register as a boss spawn → cascade ResetCore
    /// → wipe the active fight's data.
    /// </summary>
    private void TryFireBossDetected(int mobId, EntityState s, long prevHp)
    {
        if (s.BossDetected) return;
        if (s.IncomingDamageEvents == 0) return;

        // mob_code is authoritative when available — small-HP bosses (초월 1단계) still
        // qualify, big-HP trash mobs (4단계 / 성역 mid-tier) get filtered out.
        bool? known = IsKnownBoss?.Invoke(mobId);
        bool qualifies = known switch
        {
            true => true,
            false => false,
            null => s.MaxHp >= BossHpThreshold,
        };
        if (!qualifies) return;

        s.BossDetected = true;
        FocusedEntityId = mobId;
        Log("→ NEW_BOSS_FIRED", mobId, s.MaxHp, prevHp, s.CurrentHp, false, s.BossDetected);
        NewBossDetected?.Invoke(mobId);
    }

    /// <summary>Used after a session reset to re-seed boss state without losing context.</summary>
    public void RestoreEntity(int id, long maxHp, long currentHp)
    {
        var s = GetOrCreate(id);
        s.MaxHp = maxHp;
        s.CurrentHp = currentHp;
        s.BossDetected = IsBossGrade(id, s);
        s.IncomingDamageEvents = 0;
        s.IncomingDamageTotal = 0;
        FocusedEntityId = id;
    }

    public void OnDamage(DamageEvent dmg)
    {
        var s = GetOrCreate(dmg.TargetId);
        s.IncomingDamageEvents++;
        s.IncomingDamageTotal += dmg.Damage;
        s.LastUpdateUtc = DateTime.UtcNow;
        UpdateFocus();

        // If the HP packet for a boss arrived BEFORE we hit it, the new-boss-detected
        // fire was deferred (gated on IncomingDamageEvents). Now that we've damaged it
        // and the count crossed 0 → 1, retry the fire.
        TryFireBossDetected(dmg.TargetId, s, s.CurrentHp);
    }

    private EntityState GetOrCreate(int id)
    {
        if (!_entities.TryGetValue(id, out var s))
        {
            s = new EntityState();
            _entities[id] = s;
        }
        return s;
    }

    private void UpdateFocus()
    {
        // Focus = entity with most incoming damage events AMONG ALIVE entities.
        // Dead entities (CurrentHp = 0) are excluded so a freshly-killed boss
        // doesn't keep "stealing" focus away from the next boss the user starts
        // attacking. Without this skip, the leaderboard would freeze on the
        // dead boss's frozen stats while the user fights the next one.
        int? best = null;
        int bestCount = 0;
        foreach (var (id, s) in _entities)
        {
            if (s.CurrentHp == 0) continue;
            if (s.IncomingDamageEvents > bestCount)
            {
                bestCount = s.IncomingDamageEvents;
                best = id;
            }
        }
        int? prevFocus = FocusedEntityId;
        FocusedEntityId = best;

        // Diagnostic: log focus transitions so we can correlate them with
        // perceived UI flicker (e.g. "[me] 행이 깜빡거림" while the user
        // isn't attacking). If focus shifts to a non-boss-grade entity
        // briefly, IsBossMode flips false → MainViewModel hides the
        // placeholder → next focus shift back to boss flips it true →
        // placeholder re-shown.
        if (prevFocus != FocusedEntityId)
            LogFocusChange(prevFocus, FocusedEntityId);
    }

    private void LogFocusChange(int? prev, int? next)
    {
        if (string.IsNullOrEmpty(DiagnosticLogPath)) return;
        try
        {
            string prevDesc = prev?.ToString() ?? "(none)";
            string nextDesc = next?.ToString() ?? "(none)";
            bool nextIsBossGrade = next.HasValue
                && _entities.TryGetValue(next.Value, out var s)
                && IsBossGrade(next.Value, s);
            File.AppendAllText(DiagnosticLogPath,
                $"{DateTime.Now:HH:mm:ss.fff} → FOCUS_CHANGE     prev={prevDesc} next={nextDesc} nextIsBossGrade={(nextIsBossGrade ? "T" : "F")}\n");
        }
        catch { }
    }

    public EntityState? GetEntity(int id) =>
        _entities.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// True if any tracked boss-grade entity (other than <paramref name="excludeId"/>) is
    /// still alive (CurrentHp &gt; 0). Used by DpsAggregator to suppress auto-reset when
    /// a new boss is detected during an ongoing multi-boss fight — otherwise each newly
    /// observed entity would call ResetCore and wipe the others' progress.
    /// </summary>
    /// <summary>
    /// Stale entities are excluded from "alive" checks. We use this to skip ghost
    /// boss-grade entities whose KILL_FIRED never triggered (e.g., paired bosses
    /// where one half is killed off-screen / by a different mechanic / by another
    /// party — its HP never observed transitioning to 0, so the BossKilled gate
    /// never closed it). Without this expiry, the ghost stays "alive" forever and
    /// HasOtherAliveBoss returns true on every subsequent boss → ResetCore is
    /// suppressed for the rest of the dungeon run.
    /// </summary>
    /// <summary>
    /// 30s tuned: longer than typical phase-transition / invulnerable windows
    /// (so we don't false-positive a still-alive boss as expired and wipe the
    /// session) but short enough to expire ghost paired-bosses whose KILL_FIRED
    /// gate didn't close. 45s was too long (wipe + retry never reset). 15s was
    /// too short (mid-fight phase transitions tripped the timeout).
    /// </summary>
    public TimeSpan StaleEntityTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool HasOtherAliveBoss(int excludeId)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, s) in _entities)
        {
            if (id == excludeId) continue;
            if (s.CurrentHp == 0) continue;
            if (!IsBossGrade(id, s)) continue;
            // Stale-skip: no HP/damage update in StaleEntityTimeout → treat as gone.
            if (now - s.LastUpdateUtc > StaleEntityTimeout) continue;
            return true;
        }
        return false;
    }

    public void Reset()
    {
        _entities.Clear();
        FocusedEntityId = null;
    }

    public sealed class EntityState
    {
        public long MaxHp { get; set; }
        public long CurrentHp { get; set; }
        public bool BossDetected { get; set; }
        public int IncomingDamageEvents { get; set; }
        public long IncomingDamageTotal { get; set; }
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;
    }
}
