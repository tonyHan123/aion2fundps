using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace Aion2FunDps.UI.ViewModels;

public partial class PlayerRowViewModel : ObservableObject
{
    [ObservableProperty] private int rank;
    [ObservableProperty] private int actorId;
    [ObservableProperty] private string displayName = string.Empty;
    [ObservableProperty] private bool isSelf;
    [ObservableProperty] private bool isPrimaryGuess;
    [ObservableProperty] private string selfTag = string.Empty;
    [ObservableProperty] private ImageSource? classIcon;
    [ObservableProperty] private string classChar = "?";
    [ObservableProperty] private string className = "?";
    [ObservableProperty] private string classColorHex = "#555555";
    [ObservableProperty] private string topSkillsTooltip = string.Empty;
    [ObservableProperty] private long totalDamage;
    [ObservableProperty] private string totalDamageDisplay = string.Empty;  // "23.8M" compact format
    [ObservableProperty] private double dps;
    [ObservableProperty] private string dpsDisplay = string.Empty;          // "241.8K" compact format
    [ObservableProperty] private int hitCount;
    [ObservableProperty] private double critRate;
    [ObservableProperty] private double backAttackRate;
    [ObservableProperty] private double damageBarPercent;   // 0..100, relative to top player (drives outer "누적" bar width)
    [ObservableProperty] private double dpsBarPercent;       // 0..100, relative to top current DPS (drives inner DPS-intensity line width)
    [ObservableProperty] private double damageSharePercent; // 0..100, share of total displayed crew damage
    [ObservableProperty] private int combatPower;            // 0 if unknown
    [ObservableProperty] private string combatPowerDisplay = string.Empty;  // "158.5k" formatted

    // ── 앰비언트 글래스 (2026-06-12 리디자인) ────────────────────────────
    // 숫자는 값/단위 Run 2개로 분리 렌더 (단위 디밍 타이포). 한국식 만/억 단위.
    [ObservableProperty] private string brightClassColorHex = "#9AA5B2";   // 행 막대/팁 — 클래스 고정 색
    [ObservableProperty] private string dpsValue = "0";                    // "15.2"
    [ObservableProperty] private string dpsUnit = string.Empty;            // "만"
    [ObservableProperty] private string totalValue = "0";                  // "6665"
    [ObservableProperty] private string totalUnit = string.Empty;          // "만"
    [ObservableProperty] private string shareValue = "0.0";                // "11.1" (% 는 XAML 리터럴)
    [ObservableProperty] private int shareTier;                            // 0=중립 1=은 2=금 (파티 평균 대비)
}
