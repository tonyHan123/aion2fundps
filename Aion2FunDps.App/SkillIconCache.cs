using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Aion2FunDps.App;

/// <summary>
/// Lazy on-disk cache for NCSoft skill icons. Files land under
/// %LocalAppData%\aion2fundps\skill-icons\<lastSegment.png>. First request
/// for a skill kicks off an async download; subsequent requests hit disk.
/// UI consumers bind to a BitmapImage that the cache populates when the
/// download finishes — null result means "not downloaded yet, try later".
/// </summary>
public static class SkillIconCache
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "aion2fundps", "skill-icons");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    // In-memory layer so the same icon doesn't get decoded once per row.
    private static readonly ConcurrentDictionary<string, BitmapImage> _memCache = new();
    // De-dup concurrent fetch attempts on the same URL.
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> _inflight = new();

    /// <summary>
    /// Returns a cached BitmapImage if the icon is already on disk or in
    /// memory. Returns null and starts a background download on miss; the
    /// caller should fall back to a placeholder and re-query later.
    /// </summary>
    public static BitmapImage? TryGet(string iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl)) return null;
        if (_memCache.TryGetValue(iconUrl, out var cached)) return cached;

        string path = LocalPath(iconUrl);
        if (File.Exists(path))
        {
            var img = LoadFromDisk(path);
            if (img != null)
            {
                _memCache[iconUrl] = img;
                return img;
            }
        }

        _ = EnsureFetched(iconUrl);
        return null;
    }

    /// <summary>
    /// Awaitable variant for callers that want to wait for the download
    /// (e.g., breakdown window opening fresh). Hits the same dedup map so
    /// concurrent calls share one network roundtrip.
    /// </summary>
    public static Task<BitmapImage?> EnsureFetched(string iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return Task.FromResult<BitmapImage?>(null);
        if (_memCache.TryGetValue(iconUrl, out var cached))
            return Task.FromResult<BitmapImage?>(cached);

        return _inflight.GetOrAdd(iconUrl, FetchOnce);
    }

    private static async Task<BitmapImage?> FetchOnce(string iconUrl)
    {
        try
        {
            string path = LocalPath(iconUrl);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(CacheDir);
                using var resp = await _http.GetAsync(iconUrl).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                // Atomic move so a half-written file can't poison the cache
                // if the app crashes mid-download.
                string tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
            }
            var img = LoadFromDisk(path);
            if (img != null) _memCache[iconUrl] = img;
            return img;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inflight.TryRemove(iconUrl, out _);
        }
    }

    private static BitmapImage? LoadFromDisk(string path)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static string LocalPath(string iconUrl)
    {
        // Use last URL segment as filename (e.g. ICON_TE_SKILL_034.png).
        // Stable, collision-free across CDN paths because NCSoft uses
        // unique resource ids per skill.
        int slash = iconUrl.LastIndexOf('/');
        string fileName = slash >= 0 ? iconUrl[(slash + 1)..] : iconUrl;
        // Defensive sanitize against any unexpected path chars.
        foreach (var c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');
        return Path.Combine(CacheDir, fileName);
    }
}
