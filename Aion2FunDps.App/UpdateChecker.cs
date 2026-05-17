// GitHub Releases API 를 폴링해 새 알파/베타가 나오면 사용자에게 알리고
// 클릭 시 백그라운드 다운로드 + 탐색기 열어 사용자가 직접 교체하도록 안내한다.
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aion2FunDps.App;

/// <summary>
/// One-shot update check against the GitHub Releases API.
///
/// Why not a full auto-replacer (Velopack / Squirrel): a self-modify + restart
/// pattern is what ransomware and droppers look like to AV heuristics. AhnLab
/// V3 and 알약 — the Korean engines most of our users will be running — are
/// particularly aggressive on that signature, and a single false positive
/// would torch the "VirusTotal 0/70" trust line we built the alpha on. The
/// A+ design keeps the convenient one-click discovery flow (button →
/// download → "여기서 새 파일 더블클릭하세요" prompt) while letting the user
/// be the one who actually executes the new binary — neutral from an AV
/// behavioural-detection standpoint.
/// </summary>
public sealed class UpdateChecker
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/tonyHan123/aion2fundps/releases/latest";
    // GitHub requires a User-Agent on every API request; a missing one returns 403.
    private const string UserAgent = "aion2fundps-app";

    private static readonly HttpClient _http = CreateClient();

    public sealed record UpdateInfo(string Version, string DownloadUrl, string HtmlUrl, string? ExpectedSha256);

    // Release body 안에 적어둔 "SHA256: ABCDEF..." 형태 64자리 헥스 패턴.
    // 백틱 / 공백 둘러쌈 허용. 대소문자 무시.
    private static readonly Regex Sha256InBodyRegex = new(
        @"SHA256[:\s`]*([0-9A-Fa-f]{64})",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns the latest release if it is strictly newer than the current
    /// assembly's informational version, otherwise null. Returns null on any
    /// network / parse error — the meter must never block startup waiting on
    /// GitHub.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        try
        {
            using var resp = await _http.GetAsync(ReleasesApiUrl, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            string tag = tagEl.GetString() ?? "";

            string latestVer = NormalizeVersion(tag);
            string currentVer = NormalizeVersion(GetCurrentVersion());
            if (!IsNewer(latestVer, currentVer)) return null;

            string downloadUrl = "";
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameEl)) continue;
                    var name = nameEl.GetString();
                    if (name is null) continue;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (asset.TryGetProperty("browser_download_url", out var urlEl))
                    {
                        downloadUrl = urlEl.GetString() ?? "";
                        break;
                    }
                }
            }

            string htmlUrl = root.TryGetProperty("html_url", out var htmlEl)
                ? htmlEl.GetString() ?? ""
                : "";

            // Release body 에서 SHA256 추출 (있으면).
            // 기대 형식: "SHA256: `FA4EBB1C...`" — Release Description 의 다운로드 섹션에 적힌 해시.
            string? expectedSha256 = null;
            if (root.TryGetProperty("body", out var bodyEl) && bodyEl.GetString() is string body)
            {
                var match = Sha256InBodyRegex.Match(body);
                if (match.Success) expectedSha256 = match.Groups[1].Value.ToUpperInvariant();
            }

            return new UpdateInfo(tag, downloadUrl, htmlUrl, expectedSha256);
        }
        catch
        {
            // Network down, rate-limited, GitHub outage — silent failure is
            // correct here. The user can always check Releases manually.
            return null;
        }
    }

    /// <summary>
    /// Downloads the exe asset to %LocalAppData%\aion2fundps\updates\ and
    /// returns the full path. Caller is responsible for opening Explorer
    /// at this path and prompting the user to swap files manually — we
    /// deliberately do not overwrite the running executable from within
    /// the running executable.
    /// </summary>
    public static async Task<string?> DownloadAsync(string url, string? expectedSha256 = null, IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            string updateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "aion2fundps", "updates");
            Directory.CreateDirectory(updateDir);
            string dst = Path.Combine(updateDir, "Aion2FunDps.App.exe");

            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            long? total = resp.Content.Headers.ContentLength;
            using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var fs = File.Create(dst);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, token).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), token).ConfigureAwait(false);
                read += n;
                if (total is long t && t > 0)
                    progress?.Report((double)read / t);
            }
            await fs.FlushAsync(token).ConfigureAwait(false);
            fs.Close();

            // 무결성 검증: 기대 해시가 있으면 다운로드 파일 SHA256 와 비교.
            // 일치 안 하면 (MITM / 손상 / 잘못된 Release body) 파일 삭제 후 실패.
            // 기대 해시가 null 이면 (Release body 에 SHA256 명시 안 됨) 검증 스킵.
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                string actualSha256 = await ComputeSha256Async(dst, token).ConfigureAwait(false);
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(dst); } catch { }
                    return null;
                }
            }
            return dst;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken token)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, token).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Starts a tiny out-of-process replacement step, then returns immediately.
    /// The caller should close the app right after this returns true. The
    /// updater waits for the current process to exit, backs up the old exe,
    /// copies the downloaded exe into place, and relaunches the meter.
    /// </summary>
    public static bool TryApplyAndRestart(string downloadedExePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(downloadedExePath) || !File.Exists(downloadedExePath))
                return false;

            string? currentExePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
                return false;

            string updateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "aion2fundps", "updates");
            Directory.CreateDirectory(updateDir);

            string scriptPath = Path.Combine(updateDir, "apply-update.cmd");
            string logPath = Path.Combine(updateDir, "apply-update.log");
            string backupPath = currentExePath + ".bak";
            int pid = Process.GetCurrentProcess().Id;

            File.WriteAllLines(scriptPath, new[]
            {
                "@echo off",
                "setlocal",
                $"set \"SRC={downloadedExePath}\"",
                $"set \"DST={currentExePath}\"",
                $"set \"BAK={backupPath}\"",
                $"set \"LOG={logPath}\"",
                $"set \"PID={pid}\"",
                "echo === %DATE% %TIME% apply update === >> \"%LOG%\"",
                ":wait_for_exit",
                "tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul",
                "if not errorlevel 1 (",
                "  timeout /t 1 /nobreak >nul",
                "  goto wait_for_exit",
                ")",
                "if exist \"%DST%\" copy /Y \"%DST%\" \"%BAK%\" >> \"%LOG%\" 2>&1",
                "copy /Y \"%SRC%\" \"%DST%\" >> \"%LOG%\" 2>&1",
                "if errorlevel 1 (",
                "  echo copy failed, attempting rollback >> \"%LOG%\"",
                "  if exist \"%BAK%\" copy /Y \"%BAK%\" \"%DST%\" >> \"%LOG%\" 2>&1",
                "  start \"\" \"%DST%\"",
                "  exit /b 1",
                ")",
                "echo update applied >> \"%LOG%\"",
                "start \"\" \"%DST%\"",
                "del \"%~f0\" >nul 2>nul",
            });

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    private static string GetCurrentVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return info?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static string NormalizeVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return "0.0.0";
        var s = v.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        // Drop SourceLink / +build metadata; keep only the semver core.
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        return s;
    }

    /// <summary>
    /// SemVer-ish numeric compare on the major.minor.patch core. Any pre-
    /// release tag (e.g. "-alpha", "-beta") is compared lexicographically
    /// AFTER the numeric core — so 0.1.0-alpha &lt; 0.1.0 and 0.1.0 &lt;
    /// 0.2.0-alpha. Good enough for our release cadence; not full SemVer.
    /// </summary>
    private static bool IsNewer(string latest, string current)
    {
        var (lc, lp) = SplitVersion(latest);
        var (cc, cp) = SplitVersion(current);
        for (int i = 0; i < 3; i++)
        {
            int lv = i < lc.Length ? lc[i] : 0;
            int cv = i < cc.Length ? cc[i] : 0;
            if (lv > cv) return true;
            if (lv < cv) return false;
        }
        // Numeric core equal: a versioned-only release is newer than the
        // corresponding pre-release (current="0.1.0-alpha", latest="0.1.0").
        if (cp.Length > 0 && lp.Length == 0) return true;
        if (cp.Length == 0 && lp.Length > 0) return false;
        return string.CompareOrdinal(lp, cp) > 0;
    }

    private static (int[] core, string prerelease) SplitVersion(string v)
    {
        string core = v;
        string pre = "";
        int dash = v.IndexOf('-');
        if (dash >= 0) { core = v[..dash]; pre = v[(dash + 1)..]; }
        var parts = core.Split('.');
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            int.TryParse(parts[i], out nums[i]);
        return (nums, pre);
    }
}
