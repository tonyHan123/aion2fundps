using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion2FunDps.App;

/// <summary>
/// User-modifiable preferences persisted across sessions. Stored as JSON at
/// <see cref="SettingsPath"/> (%AppData%\aion2fundps\settings.json). Loaded
/// once on startup, saved on settings change + on app exit.
///
/// Defaults match the v0.1.0-alpha launch experience: Default (dark+cyan/gold)
/// theme, full opacity, auto-reset enabled, expanded layout. A first-run user
/// sees the same UI as before this settings system was introduced.
/// </summary>
public sealed class AppSettings
{
    public string SelectedTheme { get; set; } = "Default";

    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>
    /// 프레임 생성 호환 모드. true 면 AllowsTransparency=False 로 윈도우 생성 — AFMF /
    /// Lossless Scaling 같은 frame gen 툴과 호환 (대신 배경 투명 안 됨, 검은색). false (기본)
    /// 면 AllowsTransparency=True — 게임이 비치는 진짜 투명 (대다수 사용자 기본값). 변경 시
    /// 재시작 필요 (AllowsTransparency 는 윈도우 생성 시점에만 설정 가능).
    /// </summary>
    public bool FrameGenCompatMode { get; set; } = false;

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>
    /// 지분율 계산 기준.
    ///   "Party" (기본): 내 데미지 / 파티 전체 데미지 합 → 합이 항상 100%
    ///   "BossHp"      : 내 데미지 / 보스 HP 손실        → 측정 누수가 있으면 합이 100% 미만
    /// 신뢰도 100% 일 때는 두 값이 동일. 누수가 있을 때만 차이.
    /// </summary>
    public string ShareCalculationMode { get; set; } = "Party";

    /// <summary>
    /// User-customizable hotkey strings (e.g., "Ctrl+R", "Alt+M", "F8").
    /// Empty / null = no binding. Parsed by SettingsWindow when capturing
    /// input and by MainWindow when registering InputBindings on startup.
    /// </summary>
    public string? ResetHotkey { get; set; } = "Ctrl+R";
    public string? MinimizeHotkey { get; set; } = null;

    [JsonIgnore]
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "aion2fundps");

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reads settings from disk. Returns a default instance if the file
    /// doesn't exist or is malformed. Never throws — corrupted settings
    /// must not block app startup.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Persists this instance to disk. Atomic write (temp file + move) so
    /// a crash mid-write doesn't truncate the existing settings file.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            // .NET 5+ Move(overwrite:true) is atomic on Windows — no
            // delete-then-move window during which a crash leaves the user
            // without any settings file. The earlier two-step sequence lost
            // every persisted preference if the process died between steps.
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch
        {
            // Save failures are non-fatal — user can re-set preferences next session.
        }
    }
}
