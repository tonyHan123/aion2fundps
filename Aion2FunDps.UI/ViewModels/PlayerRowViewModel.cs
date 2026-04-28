using CommunityToolkit.Mvvm.ComponentModel;

namespace Aion2FunDps.UI.ViewModels;

public partial class PlayerRowViewModel : ObservableObject
{
    [ObservableProperty] private int rank;
    [ObservableProperty] private int actorId;
    [ObservableProperty] private string displayName = string.Empty;
    [ObservableProperty] private bool isSelf;
    [ObservableProperty] private bool isPrimaryGuess;
    [ObservableProperty] private string selfTag = string.Empty;
    [ObservableProperty] private string classChar = "?";
    [ObservableProperty] private string className = "?";
    [ObservableProperty] private string classColorHex = "#555555";
    [ObservableProperty] private long totalDamage;
    [ObservableProperty] private double dps;
    [ObservableProperty] private int hitCount;
    [ObservableProperty] private double critRate;
    [ObservableProperty] private double backAttackRate;
    [ObservableProperty] private double damageBarPercent;  // 0..100, relative to top player
}
