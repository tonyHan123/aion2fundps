// 사용자 진단 로그를 %LocalAppData%\aion2fundps\logs\ 로 고정하는 경로 헬퍼.
using System.IO;

namespace Aion2FunDps.App;

/// <summary>
/// Single source of truth for log file locations. Single-file Release
/// builds extract to a per-user temp dir whose path is unstable across
/// runs and effectively invisible to end users — writing logs there
/// makes alpha bug reports impossible because the reporter can't find
/// the file we ask them to attach. Anchoring at %LocalAppData% gives
/// every user the same, stable, discoverable location regardless of
/// where the single-file extractor unpacked the binary on this run.
/// </summary>
public static class LogPaths
{
    private static readonly string _root = InitializeRoot();

    public static string Root => _root;

    public static string Combine(string fileName) => Path.Combine(_root, fileName);

    private static string InitializeRoot()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "aion2fundps",
            "logs");
        try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
        return dir;
    }
}
