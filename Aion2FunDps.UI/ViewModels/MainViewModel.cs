using System.Collections.ObjectModel;
using System.Windows.Threading;
using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Core.Sessions;
using Aion2FunDps.Protocol;
using Aion2FunDps.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aion2FunDps.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DpsAggregator _aggregator;
    /// <summary>
    /// Exposes the underlying aggregator so the App layer can open auxiliary
    /// windows (skill-breakdown popup) that need PlayerStats snapshots without
    /// going through row VMs.
    /// </summary>
    public DpsAggregator Aggregator => _aggregator;
    private readonly NpcapAdapter _capture;
    private readonly FrameAssembler _assembler;
    private readonly IDispatcherTelemetry _dispatcher;
    private readonly SkillDatabase _skillDb;
    private readonly MobDatabase _mobDb;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty] private ObservableCollection<PlayerRowViewModel> players = new();

    [ObservableProperty] private double confidenceScore = 1.0;
    [ObservableProperty] private string confidenceTier = "Excellent";
    [ObservableProperty] private string confidenceEmoji = "✅";
    [ObservableProperty] private string confidenceIssues = "(이상 없음)";
    [ObservableProperty] private bool isConfidenceVisible;   // only true while a boss fight is being measured

    [ObservableProperty] private bool hasFocusedTarget;
    [ObservableProperty] private bool isBossMode;
    [ObservableProperty] private string focusInfo = "대기 중…";
    [ObservableProperty] private double bossHpPercent = 100;

    [ObservableProperty] private string sessionInfo = "0s";
    [ObservableProperty] private long totalEvents;
    [ObservableProperty] private bool tickIndicator;
    [ObservableProperty] private long lastTickTotalEvents;
    [ObservableProperty] private long eventsPerSecond;
    [ObservableProperty] private string nickDebugInfo = "nick: 0/0 self, 0/0 other";
    [ObservableProperty] private bool showAutoResetFlash;
    [ObservableProperty] private double windowOpacity = 1.0;   // 0.2..1.0, bound to Window.Opacity in XAML
    [ObservableProperty] private bool isCompact;               // collapse-to-titlebar mode (replaces native minimize)

    // Update notification surface — set by App.xaml.cs after the background
    // GitHub Releases check completes. MainWindow's title bar binds a
    // notification button to these so the user can discover and trigger an
    // update without leaving the meter.
    [ObservableProperty] private bool isUpdateAvailable;
    [ObservableProperty] private string updateVersionLabel = "";
    [ObservableProperty] private string updateDownloadUrl = "";
    [ObservableProperty] private string updateHtmlUrl = "";
    [ObservableProperty] private string updateExpectedSha256 = "";  // Release body 에서 추출. 무결성 검증용.

    /// <summary>
    /// 지분율 계산 모드 ("Party" 또는 "BossHp"). AppSettings 와 동기화.
    /// "Party"  = 본인 / 파티 합 (합 100%)
    /// "BossHp" = 본인 / 보스 HP 손실 (누수 있으면 합 100% 미만)
    /// </summary>
    [ObservableProperty] private string shareCalculationMode = "Party";

    [ObservableProperty] private string? currentDungeonName;
    public bool HasDungeon => !string.IsNullOrEmpty(CurrentDungeonName);
    partial void OnCurrentDungeonNameChanged(string? value) => OnPropertyChanged(nameof(HasDungeon));

    /// <summary>cold-start 진단: 보스 fight 중인데 PartyAssembly 미도착. UI 가 "파티 정보
    /// 불러오는 중..." 안내 표시. PartyAssembly 도착 시 자동 false.</summary>
    [ObservableProperty] private bool isResolvingParty;

    /// <summary>share% 진단 로그 path. set 되면 매 1초 share% 합 / 분모 / crew 상태 한 줄
    /// 기록. 합 &gt; 100% 폭증의 정확한 path 식별 (stabilization 가드 풀리는 시점, 분모 선택
    /// 분기, crew row 수 vs 데미지 누적 상태 등).</summary>
    public string? ShareDebugLogPath { get; set; }
    private DateTime _lastShareDebugAt = DateTime.MinValue;
    public bool IsContentVisible => !IsCompact;
    partial void OnIsCompactChanged(bool value) => OnPropertyChanged(nameof(IsContentVisible));
    private DateTime? _shownAutoResetAt;

    /// <summary>Sentinel ActorId for the "self placeholder" row shown when a boss is
    /// engaged but the user hasn't landed a hit yet. Negative so it can never collide
    /// with a real entityId from the wire.</summary>
    private const int SelfPlaceholderActorId = -1;

    public MainViewModel(
        DpsAggregator aggregator,
        NpcapAdapter capture,
        FrameAssembler assembler,
        IDispatcherTelemetry dispatcher,
        SkillDatabase skillDb,
        MobDatabase mobDb)
    {
        _aggregator = aggregator;
        _capture = capture;
        _assembler = assembler;
        _dispatcher = dispatcher;
        _skillDb = skillDb;
        _mobDb = mobDb;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
    }

    [RelayCommand]
    private void ResetSession()
    {
        // Manual reset = "I'm starting fresh / I changed rooms" — clear the
        // party-member set too so a stale roster from the previous matchmaking
        // room is wiped immediately rather than waiting for the next op=0297
        // broadcast (which can lag 16+ seconds after a real room change).
        _aggregator.ResetForNewRoom();
        Players.Clear();
    }

    private static string FormatHp(long hp) =>
        hp >= 1_000_000 ? $"{hp / 1_000_000.0:F1}M"
        : hp >= 1_000   ? $"{hp / 1_000.0:F1}K"
                        : hp.ToString("N0");

    /// <summary>Game-style combat-power format: 158,526 → "158.5k", 1,234,567 → "1.2M".</summary>
    private static string FormatCombatPower(int cp) =>
        cp >= 1_000_000 ? $"{cp / 1_000_000.0:F1}M"
        : cp >= 1_000   ? $"{cp / 1_000.0:F1}k"
                        : cp.ToString("N0");

    /// <summary>Compact damage format: 23,826,577 → "23.8M", 241,798 → "241.8K".
    /// Used in the leaderboard rows so the columns don't blur into long digit walls. </summary>
    private static string FormatCompact(long v) =>
        v >= 1_000_000 ? $"{v / 1_000_000.0:F1}M"
        : v >= 1_000   ? $"{v / 1_000.0:F1}K"
                       : v.ToString("N0");

    private static string FormatCompact(double v) =>
        v >= 1_000_000 ? $"{v / 1_000_000.0:F1}M"
        : v >= 1_000   ? $"{v / 1_000.0:F1}K"
                       : ((long)v).ToString("N0");

    /// <summary>한국식 DPS 표기 (값/단위 분리 — 단위 디밍 타이포용): 152,300 → ("15.2","만").</summary>
    private static (string Value, string Unit) FormatDpsKr(double v) =>
        v >= 100_000_000 ? ($"{v / 100_000_000.0:F2}", "억")
        : v >= 10_000    ? ($"{v / 10_000.0:F1}", "만")
                         : (((long)v).ToString("N0"), string.Empty);

    /// <summary>한국식 총딜 표기: 137,270,000 → ("1.4","억"), 66,650,000 → ("6,665","만").</summary>
    private static (string Value, string Unit) FormatTotalKr(long v) =>
        v >= 100_000_000 ? ($"{v / 100_000_000.0:F1}", "억")
        : v >= 10_000    ? ((v / 10_000).ToString("N0"), "만")
                         : (v.ToString("N0"), string.Empty);

    private void Refresh()
    {
        // Drop _partyMembers entries that have stopped appearing in any
        // broadcast (Strong / Weak / LiveStatus / damage event). The wire
        // doesn't carry a per-member "left party" packet, so this time-based
        // sweep is the canonical signal for joiner-left detection.
        // Internally gated to lobby-only — it skips during boss fights and
        // post-kill display holds so it can't prune rows mid-fight.
        _aggregator.EvictStaleMembers();

        // Phase 2 — view 빌드 (UI 는 아직 안 읽음, 검증 로그만). DEBUG 빌드에선 ViewVerificationLogPath
        // 가 박혀 1초마다 view vs mutable state diff 한 줄 기록. Release 에선 path null 이라
        // 호출 자체는 가벼움 (풀빌드 O(n) 만 발생).
        _ = _aggregator.BuildView();

        // Pull leak counters from upstream
        _aggregator.RefreshAccuracy(
            droppedPackets: _capture.Health.DroppedPackets + _capture.Health.InterfaceDroppedPackets + _capture.Health.DroppedAtChannel,
            malformedFrames: _assembler.MalformedFrames + _dispatcher.MalformedCount,
            unknownOpcodes: _dispatcher.UnknownCount);

        // Mirror the aggregator's dungeon-name state so the title bar shows
        // the current matchmaking dungeon (e.g. "무의 요람(보통)") whenever
        // the user is in a room. Cleared on PartyLeft.
        if (CurrentDungeonName != _aggregator.CurrentDungeonName)
            CurrentDungeonName = _aggregator.CurrentDungeonName;

        // Cold-start 진단 신호. 보스 fight 중인데 PartyAssembly 미도착 → 화면에
        // "파티 정보 불러오는 중..." 안내. PartyAssembly 도착하면 자동 false.
        bool resolving = _aggregator.IsResolvingParty;
        if (IsResolvingParty != resolving) IsResolvingParty = resolving;

        // Confidence — only display while a boss fight is actively being measured
        // (HasDriftSignal = boss focused + damage being tracked). In town/idle the
        // score wobbles ±2% from unrelated capture-layer noise, which confuses users
        // who interpret it as actual leak. Hide it instead of showing meaningless drift.
        var acc = _aggregator.Accuracy;
        IsConfidenceVisible = acc.HasDriftSignal;
        ConfidenceScore = acc.ConfidenceScore;
        ConfidenceTier = acc.Tier;
        ConfidenceEmoji = acc.StatusEmoji;
        var issues = string.Join(", ", acc.Issues());
        ConfidenceIssues = string.IsNullOrEmpty(issues) ? "(이상 없음)" : issues;

        // Boss / focus banner — read all four boss-state fields atomically
        // under the aggregator's state lock. The earlier separate property
        // reads could race a capture-thread OnBossKilled → ResetCore between
        // FocusedEntityId.HasValue and GetEntity(focusId), throwing
        // NullReferenceException at entity.MaxHp when the focus id was set
        // but the underlying entity got evicted mid-tick (audit 2026-05-04).
        var bossSnap = _aggregator.SnapshotBoss();
        if (bossSnap.FocusedEntityId.HasValue)
        {
            HasFocusedTarget = true;
            IsBossMode = bossSnap.IsBossMode;
            BossHpPercent = bossSnap.FocusedMaxHp > 0
                ? (double)bossSnap.FocusedCurrentHp / bossSnap.FocusedMaxHp * 100
                : 100;

            string bossName = "보스";
            if (bossSnap.FocusedMobCode is { } mobCode)
            {
                var mobInfo = _mobDb.GetByCode(mobCode);
                if (mobInfo != null) bossName = mobInfo.Name;
            }

            FocusInfo = IsBossMode
                ? $"⚔️ {bossName}  HP {FormatHp(bossSnap.FocusedCurrentHp)} / {FormatHp(bossSnap.FocusedMaxHp)}  ({BossHpPercent:F0}%)"
                : string.Empty;
        }
        else
        {
            HasFocusedTarget = false;
            IsBossMode = false;
            FocusInfo = "대기 중…";
        }

        SessionInfo = $"{(int)_aggregator.Current.Duration.TotalSeconds}s";
        NickDebugInfo = $"닉 패킷: SELF {_dispatcher.SelfNickParsed}/{_dispatcher.SelfNickSeen}  OTHER {_dispatcher.OtherNickParsed}/{_dispatcher.OtherNickSeen}";

        // Auto-reset flash: show "보스 감지! 자동 리셋" briefly after auto-reset fires
        var autoResetAt = _aggregator.LastAutoResetAt;
        if (autoResetAt.HasValue && autoResetAt != _shownAutoResetAt)
        {
            ShowAutoResetFlash = true;
            _shownAutoResetAt = autoResetAt;
        }
        else if (ShowAutoResetFlash && autoResetAt.HasValue && (DateTime.UtcNow - autoResetAt.Value).TotalSeconds > 4)
        {
            ShowAutoResetFlash = false;
        }
        var prevTotal = TotalEvents;
        TotalEvents = _aggregator.DamageEventCount + _aggregator.DotEventCount + _aggregator.HpEventCount
                    + _aggregator.NicknameEventCount + _aggregator.CombatBoundaryEventCount + _aggregator.SummonSpawnEventCount;
        EventsPerSecond = (TotalEvents - prevTotal) * 2;  // 500ms tick → x2 for per-second
        TickIndicator = !TickIndicator;  // toggle each refresh — UI can pulse on this

        // Players (OurCrew filter) — update in-place to avoid Clear/Add flicker
        // Always source rows from OurCrew (= _partyMembers + summon-resolved
        // owners) so the leaderboard stays continuous through boss
        // transitions. Damage column switches to "this boss only" while a
        // boss is engaged, falls back to TotalDamage otherwise. Earlier
        // code switched the SOURCE to BossDamageDealers on boss engage,
        // which dropped 0-damage members from the list — the user reported
        // this as "보스 치면 사람들이 다 없어졌다가 친 사람부터 나옴, 부자
        // 연스럽다". Keeping the same set of rows and just reordering by
        // boss damage is the natural behaviour they expect.
        // Post-kill hold: when LastKilledBossId is set (between OnBossKilled
        // and the next ResetCore), pin the damage column to the killed boss
        // so the leaderboard doesn't "reset" mid-victory. UpdateFocus shifts
        // focus to other alive boss-grade entities the instant the killed
        // entity zeros out (paired bosses, multi-id phase encounters like
        // 나트하라), and without this hold GetDamageToTarget(focus) returns
        // tiny per-phase totals instead of the kill-moment numbers.
        // Cleared automatically by ResetCore on NewBossDetected → boss N+1.
        // Reuse the bossSnap from earlier for coherent reads — avoid another
        // round of separate _aggregator.* property accesses that could race.
        //
        // Damage column always reflects session totals (TotalDamage / Dps).
        // The AutoResetOnBoss flag controls WHEN the session resets — on
        // NEW_BOSS_FIRED in ON mode, never in OFF mode — but the column
        // never per-target switches. Earlier code switched to
        // GetDamageToTarget(focusedBoss) while a boss was engaged, which
        // surfaced as "낮은 숫자 + 정렬이 0이면 밑으로" the moment focus
        // shifted to a new entity in multi-boss rooms (사용자 보고
        // 2026-05-14: 붉은 연심의 거울 던전, "딜이 자꾸 사라지는 것 같다").
        // The per-target switch produced no useful extra information in ON
        // mode either, because ResetCore on NEW_BOSS_FIRED already makes
        // TotalDamage start from 0 for the new fight — same numbers, just
        // visually stable as a single column instead of swapping sources.
        // Cumulative contract (OFF mode) clarified separately in 6afd751.
        int? bossTargetId = bossSnap.LastKilledBossId
            ?? (bossSnap.IsBossMode ? bossSnap.FocusedEntityId : null);
        var crew = _aggregator.OurCrew()
            .Select(p => (Player: p, Damage: p.TotalDamage, Dps: p.Dps))
            .OrderByDescending(row => row.Damage)
            .Take(10)
            .ToList();

        long topDamage = crew.FirstOrDefault().Damage;
        if (topDamage <= 0) topDamage = 1;
        double topDps = crew.Count > 0 ? crew.Max(row => row.Dps) : 0;
        if (topDps <= 0) topDps = 1;
        long totalCrewDamage = crew.Sum(row => row.Damage);
        if (totalCrewDamage <= 0) totalCrewDamage = 1;
        // 지분율 분모 — 설정 모드에 따라 분기.
        //   Party  : 파티 데미지 합 (기존, 합 100%)
        //   BossHp : 보스 HP 손실. 측정 누수가 있으면 합이 100% 미만 가능.
        // BossHp 모드여도 보스 HP 정보가 없으면 (idle / lobby) Party 모드로 폴백.
        //
        // **Post-kill 폴백** (v0.1.5 픽스): LastKilledBossId 가 박혀있으면 BossHp
        // 모드여도 강제로 Party 모드로 떨어뜨림. 처치 후 focus 가 다른 작은 HP 엔티티
        // (다음 던전의 잡몹, 잔여 entity 등) 로 옮겨가면 FocusedMaxHp 가 수백K 로
        // 떨어져서 분모가 작아짐 → 지분 % 가 천 단위 % 로 폭주 (사용자 보고
        // 2026-05-20: 13.6M / 361K * 100 = 3760% 표시). 처치 시점 분모는 다음 풀
        // 시작까지 사용 안 함 — Party 모드의 안정적 합산이 더 의미 있음.
        long bossHpLost = 0;
        bool postKill = bossSnap.LastKilledBossId.HasValue;
        bool useBossHp = ShareCalculationMode == "BossHp"
                      && !postKill
                      && bossSnap.FocusedMaxHp > 0
                      && bossSnap.FocusedMaxHp > bossSnap.FocusedCurrentHp;
        if (useBossHp)
        {
            // 다단계 보스 / phase HP 리셋 / 저감 / 오버킬 케이스에서 측정된 파티 데미지
            // 합이 보스 HP 감소량을 초과할 수 있음 (사용자 보고 2026-05-22: 79.9M 합 /
            // 38.1M HP loss = 합 209%). 측정 누수 (HP loss > damage) 일 때는 < 100%,
            // 오버킬 (damage > HP loss) 일 때는 == 100% 가 되도록 분모 하한을 파티 합으로.
            long hpLost = bossSnap.FocusedMaxHp - bossSnap.FocusedCurrentHp;
            bossHpLost = Math.Max(hpLost, totalCrewDamage);
        }

        // Post-kill 분모 freeze: OnBossKilled 시점에 캡쳐된 파티 합산. 멤버가 나가도
        // 분모 고정 → % 가 변하지 않음 (사용자 보고 2026-05-22: "한명나가니깐 내가
        // 지분율 높은쪽에 가고 막 다 엉퀸거 같아"). freeze 값이 없거나 ResetCore 후엔
        // null → 일반 totalCrewDamage 사용.
        long shareDenominator;
        if (useBossHp)
            shareDenominator = bossHpLost;
        else if (postKill && bossSnap.FrozenTotalPartyDamage is long frozen && frozen > 0)
            shareDenominator = frozen;
        else
            shareDenominator = totalCrewDamage;
        if (shareDenominator <= 0) shareDenominator = 1;

        // ResetCore 직후 share% 안정화 가드 (사용자 보고 2026-05-31: 멀티-보스 1보스 잡고
        // 새 fight 첫 1-2초 동안 본인 share=100% 폭증). totalCrewDamage 가 본인 첫 hit
        // 한 번만으로 시작 → 본인=100%, 동료 3명=0% (수학적으로 정상이지만 사용자 눈엔
        // 폭증). 1.5초간 share% 갱신 skip → 직전 fight 값 유지 → 데이터 안정 후 자연 전환.
        var lastReset = _aggregator.LastAutoResetAt;
        bool inShareStabilization = lastReset.HasValue
            && DateTime.UtcNow - lastReset.Value < TimeSpan.FromMilliseconds(1500);

        // Resolve primary (registered self OR most-hits heuristic)
        var primary = _aggregator.ResolvePrimary();
        // If primary isn't in displayed crew (e.g., a mob with high hit count), pick
        // the top-hits crew member so [me?] always lights up one row.
        if (primary != null && !crew.Any(row => row.Player.ActorId == primary.ActorId))
            primary = crew.OrderByDescending(row => row.Player.HitCount).FirstOrDefault().Player;
        bool selfIsRegistered = _aggregator.Registry.SelfUserId.HasValue;

        // Row visibility gates.
        //
        // 통과 조건 (OR):
        //   1) registry 진입 — 닉네임 매핑 확보됨. 정상 케이스.
        //   2) bossEngaged + primary guess — 보스 한참 때리는 중인데 닉 미정인
        //      dominant actor 한 명을 placeholder 로 surface.
        //   3) **(v0.1.4 신규) LooksLikePlayer + 활성 전투 컨텍스트** —
        //      skill_code prefix 11..18 (플레이어 클래스 스킬) 50%+ 누적
        //      5히트+ 인 actor 는 펫/소환수가 아닌 진짜 플레이어. registry
        //      없이도 surface. 던전 cold-start 트래시 페이즈에서 4명 파티가
        //      행 0개로 떨어지던 버그의 근본 픽스.
        //
        // PvP 안전 게이트: HasFocusedTarget 으로 "활성 전투 중" 일 때만 (3) 적용.
        // 로비 / 마을 idle 에서 누가 옆에서 PvP 해도 미터에 안 뜸.
        // (펫/소환수 phantom 행 차단은 LooksLikePlayer 의 skill-prefix
        // 휴리스틱 자체가 담당 — JobClassDetector.FromSkillCode 가
        // 펫 스킬을 Unknown 으로 분류해 50% 기준을 못 넘기게 함.)
        bool bossEngaged = bossTargetId.HasValue;
        bool inActiveCombat = bossSnap.FocusedEntityId.HasValue;
        // bossEngaged 포함시켜서 post-kill (FocusedEntityId=none 이지만 LastKilledBossId
        // 박혀있는 윈도) 에도 LooksLikePlayer actor 가 surface 유지. 사용자 보고
        // 2026-05-22: 마지막 보스 처치 후 한 명 나가면 그 행이 사라지던 문제 — registry
        // 없는 actor 가 active-combat 게이트만 만족하다 처치 후 inActiveCombat=false 가
        // 되면서 필터아웃됨.
        bool keepLooksLikePlayer = inActiveCombat || bossEngaged;
        crew = crew.Where(row =>
            _aggregator.Registry.GetEntry(row.Player.ActorId) != null
            || (bossEngaged && primary != null && primary.ActorId == row.Player.ActorId)
            || (keepLooksLikePlayer && row.Player.LooksLikePlayer))
            .ToList();

        // No UI dedupe needed: NicknameRegistry's canonical-id model collapses
        // all id-spaces (lobby / dungeon / SELF_NICK / OTHER_NICK / raid bulk)
        // for one player to a single canonical entity_id at registration time.
        // OurCrew yields exactly one row per Player by construction, so the
        // GroupBy nickname dedupe that used to live here is redundant.
        crew = crew
            .OrderByDescending(row => row.Damage)
            .ToList();
        if (crew.Count > 0)
        {
            topDamage = crew[0].Damage;
            if (topDamage <= 0) topDamage = 1;
            topDps = crew.Max(row => row.Dps);
            if (topDps <= 0) topDps = 1;
            totalCrewDamage = crew.Sum(row => row.Damage);
            if (totalCrewDamage <= 0) totalCrewDamage = 1;
            // 위에서 정한 freeze / mode 로직 재적용 — 필터 후 crew 가 줄어도 분모는
            // 같은 우선순위 (BossHp → frozen post-kill → live totalCrewDamage).
            if (useBossHp)
                shareDenominator = bossHpLost;
            else if (postKill && bossSnap.FrozenTotalPartyDamage is long frozen2 && frozen2 > 0)
                shareDenominator = frozen2;
            else
                shareDenominator = totalCrewDamage;
            if (shareDenominator <= 0) shareDenominator = 1;
        }

        // Build new view-model state
        var crewIds = new HashSet<int>(crew.Select(row => row.Player.ActorId));

        // Remove rows for actors that left the crew. The self placeholder (sentinel
        // ActorId) is managed at the end of Refresh — skip it here so it doesn't
        // get torn down and rebuilt every tick (flicker).
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            if (Players[i].ActorId == SelfPlaceholderActorId) continue;
            if (!crewIds.Contains(Players[i].ActorId))
                Players.RemoveAt(i);
        }

        // Update existing or insert new.
        // classCounter tracks unnamed actors per detected job class so the
        // fallback display "마도성" / "검성" 등이 동일 직업 2명 케이스에서 "마도성2"
        // 같이 disambiguate 됨. cold-start mid-dungeon 에서 OTHER_NICK / SELF_NICK
        // 패킷이 영영 안 오는 상황 (사용자 보고 2026-05-19 21:1x: "겜 중간에 켰는데
        // Actor 만 뜬다") 의 가독성 개선 — 진짜 닉네임은 모르지만 직업이라도 보여
        // "내가 누구를 보고 있는지" 즉시 파악 가능.
        var classCounter = new Dictionary<JobClass, int>();
        int rank = 0;
        foreach (var row in crew)
        {
            rank++;
            var p = row.Player;
            var registeredName = _aggregator.Registry.GetName(p.ActorId);
            var entry = _aggregator.Registry.GetEntry(p.ActorId);
            bool isSelf = entry?.IsSelf == true;
            // [me] tag only meaningful during boss fights. In town the unnamed-actor
            // heuristic might pick a random PvP duelist and falsely tag them.
            bool isPrimaryGuessRow = !isSelf && !selfIsRegistered && primary != null
                                  && primary.ActorId == p.ActorId && bossEngaged;

            // Class detection (moved up — used for name fallback as well as icon).
            //   1. Registry's Job field — populated from op=0297 / LiveStatus
            //      packets at matchmaking-room entry, so the icon shows up
            //      BEFORE any damage tick (previously the leaderboard sat
            //      iconless until first boss hit, which the user reported
            //      as "원정 대기/입장 때 클래스 이미지 안 뜸").
            //   2. Skill-code prefix fallback — for actors whose Job byte
            //      didn't make it (truncated packet, unknown layout) but
            //      whose damage skills reveal the class.
            JobClass detectedClass = JobClass.Unknown;
            if (entry?.Job is int gameJob && gameJob > 0)
                detectedClass = JobClassDetector.FromGameJobCode(gameJob);
            if (detectedClass == JobClass.Unknown)
                detectedClass = JobClassDetector.Detect(p.Skills.Keys);

            // Display name resolution priority:
            //   1) registered nickname (정상 케이스)
            //   2) "나" — heuristic primary guess during boss fight
            //   3) Korean class name (cold-start fallback — "마도성", "검성", etc.)
            //      with index suffix when same class repeats: "마도성2".
            //   4) "Actor_<id>" — last resort when class also unknown (펫? 미식별 actor?).
            string name;
            if (registeredName != null)
            {
                name = registeredName;
            }
            else if (isPrimaryGuessRow)
            {
                name = "나";
            }
            else if (detectedClass != JobClass.Unknown)
            {
                classCounter.TryGetValue(detectedClass, out int sameClassSeen);
                classCounter[detectedClass] = sameClassSeen + 1;
                var koreanClass = JobClassDetector.GetKoreanName(detectedClass);
                name = sameClassSeen == 0 ? koreanClass : $"{koreanClass}{sameClassSeen + 1}";
            }
            else
            {
                name = $"Actor_{p.ActorId}";
            }

            // Server suffix — matches the in-game lobby convention
            // ("릴캐[바바]" for cross-server members). Show for every known
            // serverId except the user's own row. Self-detection cascades:
            // (1) IsSelf flag from SELF_NICK, (2) nickname match against the
            // inferred SelfNickname (op=0297 multi-room heuristic), (3) if
            // SelfServerId is known and member's server matches, that's also
            // self. Without (2)/(3) the suffix would lag until the score-based
            // self detection converged, which is what 사용자 reported as
            // "닉보다 늦게 뜸".
            int? memberServerId = entry?.Server;
            string? inferredSelfNick = _aggregator.Registry.SelfNickname;
            int? selfServerId = _aggregator.Registry.SelfServerId;
            bool nameLooksLikeSelf = inferredSelfNick != null
                                  && string.Equals(registeredName, inferredSelfNick, StringComparison.Ordinal);
            bool serverMatchesSelf = memberServerId is int s1 && selfServerId is int s2
                                  && s1 > 0 && s1 == s2 && nameLooksLikeSelf;
            bool isSelfRow = isSelf || nameLooksLikeSelf || serverMatchesSelf;
            if (!isSelfRow
                && memberServerId is int memberSid && memberSid > 0
                && Aion2FunDps.Core.Models.ServerMap.GetShortName(memberSid) is string suffix)
            {
                name = $"{name}[{suffix}]";
            }

            var vm = Players.FirstOrDefault(v => v.ActorId == p.ActorId);
            if (vm == null)
            {
                vm = new PlayerRowViewModel { ActorId = p.ActorId };
                Players.Add(vm);
            }
            // (detectedClass 위에서 이미 계산됨 — name fallback 에 사용했고 아래
            //  vm.ClassColorHex 등 setter 에도 그대로 재사용.)

            // Build tooltip showing top skills with names — helps user verify class detection.
            // 전투력은 앰비언트 글래스 리디자인에서 행 컬럼 → 툴팁으로 이동 (행 밀도 절감).
            var topSkills = p.Skills.Values
                .OrderByDescending(s => s.TotalDamage)
                .Take(8)
                .Select(s => {
                    var info = _skillDb.Resolve((int)s.SkillCode);
                    var skillName = info?.Name ?? $"#{s.SkillCode}";
                    return $"{skillName}: {s.TotalDamage:N0} ({s.HitCount}회)";
                });
            int cpForTip = entry?.CombatPower ?? 0;
            string cpLine = cpForTip > 0 ? $"전투력: {FormatCombatPower(cpForTip)}\n" : string.Empty;
            string skillsTip = $"감지: {JobClassDetector.GetKoreanName(detectedClass)}\n" + cpLine +
                               string.Join("\n", topSkills);

            vm.Rank = rank;
            vm.DisplayName = name;
            vm.IsSelf = isSelf;
            vm.IsPrimaryGuess = isPrimaryGuessRow;
            vm.SelfTag = (isSelf || isPrimaryGuessRow) ? "[me]" : string.Empty;
            vm.ClassIcon = ClassIconFactory.GetIcon(detectedClass);
            vm.ClassChar = JobClassDetector.GetShortChar(detectedClass);
            vm.ClassName = JobClassDetector.GetKoreanName(detectedClass);
            vm.ClassColorHex = JobClassDetector.GetColorHex(detectedClass);
            vm.BrightClassColorHex = JobClassDetector.GetBrightColorHex(detectedClass);
            vm.TopSkillsTooltip = skillsTip;
            vm.TotalDamage = row.Damage;
            vm.TotalDamageDisplay = FormatCompact(row.Damage);
            (vm.TotalValue, vm.TotalUnit) = FormatTotalKr(row.Damage);
            vm.Dps = row.Dps;
            vm.DpsDisplay = FormatCompact(row.Dps);
            (vm.DpsValue, vm.DpsUnit) = FormatDpsKr(row.Dps);
            vm.HitCount = p.HitCount;
            vm.CritRate = p.CritRate;
            vm.BackAttackRate = p.BackAttackRate;
            vm.DamageBarPercent = (double)row.Damage / topDamage * 100;
            vm.DpsBarPercent = row.Dps / topDps * 100;
            // stabilization 윈도 안에서는 share% 갱신 skip — 이전 값 유지 (직전 fight 값
            // 잠깐 보이다가 안정화 후 새 값으로 자연 전환).
            if (!inShareStabilization)
            {
                // postKill 구간에선 분자도 kill-moment snapshot 사용 — 분모 (FrozenTotalPartyDamage)
                // 와 같은 시점이라 합 = 100% 보장. 쫄몹/다음보스 데미지가 row.Damage 에 누적
                // 돼도 share% 폭증 없음. snapshot 0 (= 처치 시점 멤버 X 또는 데미지 0) 이면
                // live damage fallback.
                long shareNumerator = (postKill && p.FrozenDamageForShare > 0)
                    ? p.FrozenDamageForShare
                    : row.Damage;
                vm.DamageSharePercent = (double)shareNumerator / shareDenominator * 100;
            }
            vm.ShareValue = vm.DamageSharePercent.ToString("F1");
            // 지분 히트 티어 (B-3, 2026-06-12) — 고정 % 가 아니라 파티 평균 대비 상대값.
            // 4인팟이든 8인팟이든 "평균의 1.4배 이상 = 캐리(금) / 0.85배 이상 = 은 / 그 외 중립".
            double avgShare = 100.0 / Math.Max(1, crew.Count);
            vm.ShareTier = vm.DamageSharePercent >= avgShare * 1.4 ? 2
                         : vm.DamageSharePercent >= avgShare * 0.85 ? 1 : 0;
            int cp = entry?.CombatPower ?? 0;
            vm.CombatPower = cp;
            vm.CombatPowerDisplay = cp > 0 ? FormatCombatPower(cp) : string.Empty;
        }

        // share% 진단 로그 — 1초마다 한 줄. 합 > 100% 폭증 시점 / 분모 선택 / crew 상태 식별.
        if (ShareDebugLogPath != null)
        {
            var nowDbg = DateTime.UtcNow;
            if (nowDbg - _lastShareDebugAt >= TimeSpan.FromSeconds(1))
            {
                _lastShareDebugAt = nowDbg;
                try
                {
                    double sharePctSum = 0;
                    int rowsCounted = 0;
                    foreach (var p in Players)
                    {
                        if (p.ActorId == SelfPlaceholderActorId) continue;
                        sharePctSum += p.DamageSharePercent;
                        rowsCounted++;
                    }
                    string denomSource = useBossHp ? "bossHp"
                        : (postKill && bossSnap.FrozenTotalPartyDamage.HasValue && bossSnap.FrozenTotalPartyDamage.Value > 0 ? "frozen" : "live");
                    string resetStr = lastReset.HasValue ? lastReset.Value.ToString("HH:mm:ss.fff") : "null";
                    System.IO.File.AppendAllText(ShareDebugLogPath,
                        $"{DateTime.Now:HH:mm:ss.fff} crew={crew.Count} rows={rowsCounted} denom={shareDenominator}({denomSource}) totalCrew={totalCrewDamage} sumPct={sharePctSum:F1} stab={(inShareStabilization?"T":"F")} resetAt={resetStr}\n");
                }
                catch { }
            }
        }

        // Sort: ObservableCollection doesn't have Sort, but we can ensure order via Move
        for (int i = 0; i < crew.Count; i++)
        {
            var expected = crew[i].Player.ActorId;
            int currentIdx = -1;
            for (int j = i; j < Players.Count; j++)
                if (Players[j].ActorId == expected) { currentIdx = j; break; }
            if (currentIdx >= 0 && currentIdx != i)
                Players.Move(currentIdx, i);
        }

        // Self placeholder: boss is engaged but the user has not yet landed a hit
        // (their PlayerStats doesn't exist), so no real "[me]" row is in the list.
        // Show a 0/0/0% placeholder so the user sees they're being tracked.
        // (bossEngaged was declared earlier when filtering crew.)
        //
        // CRITICAL: exclude the placeholder itself from the meRowExists check.
        // The placeholder sets IsPrimaryGuess=true on itself, so without this
        // exclusion the show/hide branches form a self-loop that toggles each
        // tick (사용자 보고: 본인이 안 때리고 파티원만 때리는 동안 "나" 행이
        // 500ms마다 깜빡거림 — 로그에서 PLACEHOLDER_SHOWN/HIDDEN 정확히
        // 매 0.5초 교차 확인).
        bool meRowExists = Players.Any(v =>
            v.ActorId != SelfPlaceholderActorId
            && (v.IsSelf || v.IsPrimaryGuess));
        var existingPlaceholder = Players.FirstOrDefault(v => v.ActorId == SelfPlaceholderActorId);

        if (bossEngaged && !meRowExists)
        {
            bool wasNew = existingPlaceholder == null;
            var ph = existingPlaceholder ?? new PlayerRowViewModel { ActorId = SelfPlaceholderActorId };
            if (wasNew) Players.Add(ph);

            ph.Rank = Players.Count;
            ph.DisplayName = "나";
            ph.IsSelf = false;
            ph.IsPrimaryGuess = true;   // makes the row light up like the real "me" row
            ph.SelfTag = "[me]";
            ph.ClassIcon = ClassIconFactory.GetIcon(JobClass.Unknown);
            ph.ClassChar = "?";
            ph.ClassName = "?";
            ph.ClassColorHex = JobClassDetector.GetColorHex(JobClass.Unknown);
            ph.TopSkillsTooltip = "첫 타격 대기 중…";
            ph.TotalDamage = 0;
            ph.TotalDamageDisplay = "0";
            ph.Dps = 0;
            ph.DpsDisplay = "0";
            ph.BrightClassColorHex = JobClassDetector.GetBrightColorHex(JobClass.Unknown);
            ph.DpsValue = "0";
            ph.DpsUnit = string.Empty;
            ph.TotalValue = "0";
            ph.TotalUnit = string.Empty;
            ph.ShareValue = "0.0";
            ph.ShareTier = 0;
            ph.HitCount = 0;
            ph.CritRate = 0;
            ph.BackAttackRate = 0;
            ph.DamageBarPercent = 0;
            ph.DpsBarPercent = 0;
            ph.DamageSharePercent = 0;
            if (wasNew)
                LogPlaceholderTransition("SHOWN", bossTargetId, meRowExists);
        }
        else if (existingPlaceholder != null)
        {
            // User has hit OR boss is no longer engaged — drop the placeholder.
            Players.Remove(existingPlaceholder);
            LogPlaceholderTransition("HIDDEN", bossTargetId, meRowExists);
        }
    }

    /// <summary>
    /// Diagnostic write to reset-debug.log capturing every placeholder
    /// show/hide transition + the boss/membership context that triggered
    /// it. Designed to find the cause of "[me] 행이 깜빡거림" — pairs of
    /// SHOWN/HIDDEN within ~500ms (one UI tick) means the source signal
    /// (bossEngaged or meRowExists) is flapping.
    /// </summary>
    private void LogPlaceholderTransition(string state, int? bossTargetId, bool meRowExists)
    {
        var path = _aggregator.Boss.DiagnosticLogPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            int? focus = _aggregator.Boss.FocusedEntityId;
            bool isBossMode = _aggregator.Boss.IsBossMode;
            System.IO.File.AppendAllText(path,
                $"{DateTime.Now:HH:mm:ss.fff} → PLACEHOLDER_{state,-6} bossTarget={bossTargetId?.ToString() ?? "(none)"} focus={focus?.ToString() ?? "(none)"} isBossMode={(isBossMode ? "T" : "F")} meRowExists={(meRowExists ? "T" : "F")}\n");
        }
        catch { }
    }
}
