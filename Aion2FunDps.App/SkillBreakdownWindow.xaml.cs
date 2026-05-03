using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2FunDps.Core;
using Aion2FunDps.Core.Sessions;

namespace Aion2FunDps.App;

public partial class SkillBreakdownWindow : Window
{
    private readonly DpsAggregator _aggregator;
    private readonly int _entityId;
    private readonly ImageSource? _classIconFallback;
    private readonly DispatcherTimerWrapper _refreshTimer;

    public SkillBreakdownWindow(DpsAggregator aggregator, int entityId, string nickname, ImageSource? classIcon)
    {
        InitializeComponent();
        _aggregator = aggregator;
        _entityId = entityId;
        _classIconFallback = classIcon;

        PlayerNameText.Text = nickname;
        if (classIcon != null) ClassIconImg.Source = classIcon;

        // Refresh on a timer so live updates flow through while the window is
        // open. 250ms matches the main meter's tick — anything faster is
        // wasted (skill totals don't change visibly per-frame), slower feels
        // laggy when the user is watching DPS climb during a pull.
        _refreshTimer = new DispatcherTimerWrapper(TimeSpan.FromMilliseconds(250), Refresh);
        _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();

        Refresh();
    }

    private void Refresh()
    {
        var stats = _aggregator.Current.GetExisting(_entityId);
        if (stats == null)
        {
            // Player got merged into a different canonical id (orphan→canon
            // collapse). Close gracefully instead of showing stale data.
            SkillList.ItemsSource = null;
            PlayerSummaryText.Text = "데이터 없음";
            return;
        }

        long totalDamage = stats.TotalDamage;
        var rows = new List<SkillRow>();

        foreach (var kv in stats.Skills)
        {
            var s = kv.Value;
            if (s.HitCount == 0) continue;

            // Name: catalog hit → real Korean name. Miss → show as "기타 효과
            // (#code)" so the user knows it's a real damage source we just
            // can't identify (item proc / stigma / NPC effect / etc.).
            // The bare "#code" was confusing — looked like a bug rather than
            // an unmapped wire field.
            string rawName = SkillCatalog.GetName(s.SkillCode);
            string name = rawName.StartsWith('#')
                ? $"기타 효과 ({rawName})"
                : rawName;

            // Icon: prefer the per-skill icon; fall back to the player's
            // class icon when the catalog has no entry (A2Viewer's icon JSON
            // only covers a subset — verified 2026-05-03: 파열격/공명쇄 등이
            // CSV엔 있지만 JSON엔 누락). Class fallback keeps the column
            // visually consistent instead of showing empty squares.
            string? iconUrl = SkillCatalog.GetIconUrl(s.SkillCode);
            ImageSource? icon = iconUrl != null
                ? (ImageSource?)SkillIconCache.TryGet(iconUrl)
                : null;
            icon ??= _classIconFallback;

            rows.Add(new SkillRow(
                Code: s.SkillCode,
                Name: name,
                Icon: icon,
                Hits: s.HitCount,
                CritRate: s.CritRate,
                TotalDamage: s.TotalDamage,
                Share: totalDamage > 0 ? (double)s.TotalDamage / totalDamage : 0,
                IconUrl: iconUrl));
        }

        rows.Sort((a, b) => b.TotalDamage.CompareTo(a.TotalDamage));

        // Bar widths normalised against top skill so the visual comparison
        // is within-window, like the main leaderboard normalises against the
        // top player.
        long topDamage = rows.Count > 0 ? rows[0].TotalDamage : 0;
        if (topDamage > 0)
        {
            const double maxBarWidth = 540;
            foreach (var r in rows)
                r.BarWidth = maxBarWidth * (double)r.TotalDamage / topDamage;
        }

        // Kick off icon downloads for any rows without a cached icon. The
        // next tick (250ms) will pick them up via SkillIconCache.TryGet.
        foreach (var r in rows)
        {
            if (r.Icon == null && !string.IsNullOrEmpty(r.IconUrl))
                _ = SkillIconCache.EnsureFetched(r.IconUrl);
        }

        SkillList.ItemsSource = rows;

        PlayerSummaryText.Text =
            $"총 피해량 {totalDamage:N0}   |   타격 {stats.HitCount:N0}   " +
            $"|   크리율 {stats.CritRate * 100:F1}%";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class SkillRow
    {
        public uint Code { get; }
        public string Name { get; }
        public ImageSource? Icon { get; }
        public int Hits { get; }
        public double CritRate { get; }
        public long TotalDamage { get; }
        public double Share { get; }
        public string? IconUrl { get; }
        public double BarWidth { get; set; }

        public SkillRow(uint Code, string Name, ImageSource? Icon, int Hits,
                        double CritRate, long TotalDamage, double Share, string? IconUrl)
        {
            this.Code = Code;
            this.Name = Name;
            this.Icon = Icon;
            this.Hits = Hits;
            this.CritRate = CritRate;
            this.TotalDamage = TotalDamage;
            this.Share = Share;
            this.IconUrl = IconUrl;
        }

        public string CritRateDisplay => $"{CritRate * 100:F0}%";
        public string ShareDisplay => $"{Share * 100:F1}%";
    }

    /// <summary>
    /// Thin wrapper that hides DispatcherTimer ceremony — captures Tick into
    /// a single Action callback and exposes Start/Stop. Internal to this
    /// window since it isn't reused elsewhere.
    /// </summary>
    private sealed class DispatcherTimerWrapper
    {
        private readonly System.Windows.Threading.DispatcherTimer _t;

        public DispatcherTimerWrapper(TimeSpan interval, Action onTick)
        {
            _t = new System.Windows.Threading.DispatcherTimer { Interval = interval };
            _t.Tick += (_, _) => onTick();
        }

        public void Start() => _t.Start();
        public void Stop() => _t.Stop();
    }
}
