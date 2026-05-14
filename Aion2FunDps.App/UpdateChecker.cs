// GitHub Releases API 를 폴링해 새 알파/베타가 나오면 사용자에게 알리고
// 클릭 시 백그라운드 다운로드 + 탐색기 열어 사용자가 직접 교체하도록 안내한다.
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

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

    public sealed record UpdateInfo(string Version, string DownloadUrl, string HtmlUrl);

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

            return new UpdateInfo(tag, downloadUrl, htmlUrl);
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
    public static async Task<string?> DownloadAsync(string url, IProgress<double>? progress = null, CancellationToken token = default)
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
            return dst;
        }
        catch
        {
            return null;
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
