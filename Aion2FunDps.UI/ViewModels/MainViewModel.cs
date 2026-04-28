using System.Collections.ObjectModel;
using System.Windows.Threading;
using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aion2FunDps.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DpsAggregator _aggregator;
    private readonly NpcapAdapter _capture;
    private readonly FrameAssembler _assembler;
    private readonly PacketDispatcher _dispatcher;
    private readonly SkillDatabase _skillDb;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty] private ObservableCollection<PlayerRowViewModel> players = new();

    [ObservableProperty] private double confidenceScore = 1.0;
    [ObservableProperty] private string confidenceTier = "Excellent";
    [ObservableProperty] private string confidenceEmoji = "✅";
    [ObservableProperty] private string confidenceIssues = "(이상 없음)";

    [ObservableProperty] private bool hasFocusedTarget;
    [ObservableProperty] private bool isBossMode;
    [ObservableProperty] private string focusInfo = "대기 중…";
    [ObservableProperty] private double bossHpPercent = 100;

    [ObservableProperty] private string sessionInfo = "0s";
    [ObservableProperty] private long totalEvents;
    [ObservableProperty] private bool tickIndicator;
    [ObservableProperty] private long lastTickTotalEvents;
    [ObservableProperty] private long eventsPerSecond;

    public MainViewModel(
        DpsAggregator aggregator,
        NpcapAdapter capture,
        FrameAssembler assembler,
        PacketDispatcher dispatcher,
        SkillDatabase skillDb)
    {
        _aggregator = aggregator;
        _capture = capture;
        _assembler = assembler;
        _dispatcher = dispatcher;
        _skillDb = skillDb;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
    }

    private void Refresh()
    {
        // Pull leak counters from upstream
        _aggregator.RefreshAccuracy(
            droppedPackets: _capture.Health.DroppedPackets + _capture.Health.InterfaceDroppedPackets + _capture.Health.DroppedAtChannel,
            malformedFrames: _assembler.MalformedFrames + _dispatcher.MalformedCount,
            unknownOpcodes: _dispatcher.UnknownCount);

        // Confidence
        var acc = _aggregator.Accuracy;
        ConfidenceScore = acc.ConfidenceScore;
        ConfidenceTier = acc.Tier;
        ConfidenceEmoji = acc.StatusEmoji;
        var issues = string.Join(", ", acc.Issues());
        ConfidenceIssues = string.IsNullOrEmpty(issues) ? "(이상 없음)" : issues;

        // Boss / focus banner
        if (_aggregator.Boss.FocusedEntityId.HasValue)
        {
            int focusId = _aggregator.Boss.FocusedEntityId.Value;
            var entity = _aggregator.Boss.GetEntity(focusId)!;
            HasFocusedTarget = true;
            IsBossMode = _aggregator.Boss.IsBossMode;
            BossHpPercent = entity.MaxHp > 0 ? (double)entity.CurrentHp / entity.MaxHp * 100 : 100;

            FocusInfo = IsBossMode
                ? $"⚔️ 보스 — Entity_{focusId}  HP {entity.CurrentHp:N0} / {entity.MaxHp:N0}  ({BossHpPercent:F1}% 남음)"
                : $"필드 사냥 — Entity_{focusId} (max HP {entity.MaxHp:N0})";
        }
        else
        {
            HasFocusedTarget = false;
            IsBossMode = false;
            FocusInfo = "대기 중…";
        }

        SessionInfo = $"{(int)_aggregator.Current.Duration.TotalSeconds}s";
        var prevTotal = TotalEvents;
        TotalEvents = _aggregator.DamageEventCount + _aggregator.DotEventCount + _aggregator.HpEventCount
                    + _aggregator.NicknameEventCount + _aggregator.CombatBoundaryEventCount + _aggregator.SummonSpawnEventCount;
        EventsPerSecond = (TotalEvents - prevTotal) * 2;  // 500ms tick → x2 for per-second
        TickIndicator = !TickIndicator;  // toggle each refresh — UI can pulse on this

        // Players (OurCrew filter) — update in-place to avoid Clear/Add flicker
        var crew = _aggregator.OurCrew()
                              .OrderByDescending(p => p.TotalDamage)
                              .Take(10)
                              .ToList();
        long topDamage = crew.FirstOrDefault()?.TotalDamage ?? 1;
        if (topDamage <= 0) topDamage = 1;

        // Resolve primary (registered self OR most-hits heuristic)
        var primary = _aggregator.ResolvePrimary();
        bool selfIsRegistered = _aggregator.Registry.SelfUserId.HasValue;

        // Build new view-model state
        var crewIds = new HashSet<int>(crew.Select(p => p.ActorId));

        // Remove rows for actors that left the crew
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            if (!crewIds.Contains(Players[i].ActorId))
                Players.RemoveAt(i);
        }

        // Update existing or insert new
        int rank = 0;
        foreach (var p in crew)
        {
            rank++;
            var name = _aggregator.Registry.GetName(p.ActorId) ?? $"Actor_{p.ActorId}";
            var entry = _aggregator.Registry.GetEntry(p.ActorId);
            bool isSelf = entry?.IsSelf == true;

            var vm = Players.FirstOrDefault(v => v.ActorId == p.ActorId);
            if (vm == null)
            {
                vm = new PlayerRowViewModel { ActorId = p.ActorId };
                Players.Add(vm);
            }
            bool isPrimaryGuess = !isSelf && !selfIsRegistered && primary != null && primary.ActorId == p.ActorId;

            vm.Rank = rank;
            vm.DisplayName = name;
            vm.IsSelf = isSelf;
            vm.IsPrimaryGuess = isPrimaryGuess;
            vm.SelfTag = isSelf ? "[me]" : isPrimaryGuess ? "[me?]" : string.Empty;
            vm.TotalDamage = p.TotalDamage;
            vm.Dps = p.Dps;
            vm.HitCount = p.HitCount;
            vm.CritRate = p.CritRate;
            vm.BackAttackRate = p.BackAttackRate;
            vm.DamageBarPercent = (double)p.TotalDamage / topDamage * 100;
        }

        // Sort: ObservableCollection doesn't have Sort, but we can ensure order via Move
        for (int i = 0; i < crew.Count; i++)
        {
            var expected = crew[i].ActorId;
            int currentIdx = -1;
            for (int j = i; j < Players.Count; j++)
                if (Players[j].ActorId == expected) { currentIdx = j; break; }
            if (currentIdx >= 0 && currentIdx != i)
                Players.Move(currentIdx, i);
        }
    }
}
