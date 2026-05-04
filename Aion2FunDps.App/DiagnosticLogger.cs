using System.IO;
using System.Text;
using Aion2FunDps.Capture;
using Aion2FunDps.Core;
using Aion2FunDps.Core.Sessions;
using Aion2FunDps.Protocol;
using Aion2FunDps.Storage.Databases;

namespace Aion2FunDps.App;

/// <summary>
/// Writes diagnostic logs at boss-kill time so the user can review post-raid where
/// damage leaked, which opcodes are unhandled, and per-skill performance per player.
///
/// Two output files (next to the .exe, append-only):
///   boss-kills.log       — one compact line per kill (date, boss, drift%, drops, top unknown)
///   session-summary.log  — verbose per-fight dump (every player + per-skill histogram)
/// </summary>
public sealed class DiagnosticLogger
{
    private readonly DpsAggregator _aggregator;
    private readonly IDispatcherTelemetry _dispatcher;
    private readonly NpcapAdapter _capture;
    private readonly FrameAssembler _assembler;
    private readonly MobDatabase _mobDb;
    private readonly SkillDatabase _skillDb;

    private readonly string _killsLogPath;
    private readonly string _summaryLogPath;
    private readonly object _writeLock = new();

    public DiagnosticLogger(
        DpsAggregator aggregator,
        IDispatcherTelemetry dispatcher,
        NpcapAdapter capture,
        FrameAssembler assembler,
        MobDatabase mobDb,
        SkillDatabase skillDb)
    {
        _aggregator = aggregator;
        _dispatcher = dispatcher;
        _capture = capture;
        _assembler = assembler;
        _mobDb = mobDb;
        _skillDb = skillDb;

        _killsLogPath = Path.Combine(AppContext.BaseDirectory, "boss-kills.log");
        _summaryLogPath = Path.Combine(AppContext.BaseDirectory, "session-summary.log");

        _aggregator.Boss.BossKilled += OnBossKilled;
    }

    private void OnBossKilled(int bossId)
    {
        try
        {
            string bossName = ResolveBossName(bossId);
            string mobCodeText = _aggregator.Entities.GetMobCode(bossId)?.ToString() ?? "none";
            var entity = _aggregator.Boss.GetEntity(bossId);
            long maxHp = entity?.MaxHp ?? 0;

            // How much damage we credited to this entity vs. how much HP actually disappeared.
            // The HP delta is just MaxHp here because CurrentHp transitioned to 0.
            long ourDamage = 0;
            foreach (var p in _aggregator.Current.AllPlayers)
            {
                if (p.DamagePerTarget.TryGetValue(bossId, out var d))
                    ourDamage += d;
            }
            long hpDelta = maxHp;
            double underRecorded = hpDelta > 0
                ? Math.Max(0, (double)(hpDelta - ourDamage) / hpDelta)
                : 0;
            double overRecorded = hpDelta > 0
                ? Math.Max(0, (double)(ourDamage - hpDelta) / hpDelta)
                : 0;

            long drops = _capture.Health.DroppedPackets
                       + _capture.Health.InterfaceDroppedPackets
                       + _capture.Health.DroppedAtChannel;
            long malformed = _assembler.MalformedFrames + _dispatcher.MalformedCount;
            long unknown = _dispatcher.UnknownCount;

            var topUnknown = _dispatcher.UnknownByOpcode
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{(byte)(kv.Key >> 8):x2}{(byte)(kv.Key & 0xff):x2}={kv.Value}")
                .ToArray();
            string topUnknownStr = topUnknown.Length == 0 ? "-" : string.Join(",", topUnknown);

            lock (_writeLock)
            {
                File.AppendAllText(_killsLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] " +
                    $"\"{bossName}\" hp={hpDelta:N0} recorded={ourDamage:N0} " +
                    $"entity={bossId} mobCode={mobCodeText} " +
                    $"under={underRecorded:P1} over={overRecorded:P1} " +
                    $"drops={drops} malformed={malformed} unknown={unknown} " +
                    $"top_unknown=[{topUnknownStr}]\n");

                WriteSessionSummary(bossId, bossName, mobCodeText, hpDelta, ourDamage, underRecorded, overRecorded, drops, malformed, unknown, topUnknownStr);
            }
        }
        catch (Exception ex)
        {
            // Diagnostics must never crash the meter — swallow + log to a sidecar file
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "diag-errors.log"),
                    $"{DateTime.Now:HH:mm:ss} OnBossKilled failed: {ex}\n");
            }
            catch { }
        }
    }

    private string ResolveBossName(int bossId)
    {
        if (_aggregator.Entities.GetMobCode(bossId) is { } mobCode)
        {
            var info = _mobDb.GetByCode(mobCode);
            if (info != null) return info.Name;
        }
        return $"#{bossId}";
    }

    private void WriteSessionSummary(int bossId, string bossName, string mobCodeText, long hpDelta, long ourDamage,
                                     double underRecorded, double overRecorded,
                                     long drops, long malformed, long unknown, string topUnknownStr)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  KILL: \"{bossName}\" ===");
        sb.AppendLine($"  entity={bossId}  mobCode={mobCodeText}");
        sb.AppendLine($"  HP_max={hpDelta:N0}  recorded={ourDamage:N0}  under={underRecorded:P1}  over={overRecorded:P1}");
        sb.AppendLine($"  drops={drops}  malformed={malformed}  unknown={unknown}");
        sb.AppendLine($"  top_unknown=[{topUnknownStr}]");
        sb.AppendLine($"  session_duration={(int)_aggregator.Current.Duration.TotalSeconds}s");
        sb.AppendLine();

        // Sort players by total damage (highest first)
        var ordered = _aggregator.Current.AllPlayers
            .Select(p => new { Player = p, BossDamage = p.GetDamageToTarget(bossId), BossDps = p.GetDpsToTarget(bossId) })
            .Where(row => row.BossDamage > 0)
            .OrderByDescending(row => row.BossDamage)
            .ToList();

        foreach (var row in ordered)
        {
            var p = row.Player;
            var name = _aggregator.Registry.GetName(p.ActorId) ?? $"Actor_{p.ActorId}";
            var cls = JobClassDetector.GetKoreanName(JobClassDetector.Detect(p.Skills.Keys));
            double crit = p.HitCount == 0 ? 0 : (double)p.CritCount / p.HitCount;
            double ba = p.HitCount == 0 ? 0 : (double)p.BackAttackCount / p.HitCount;

            sb.AppendLine($"  [{cls}] {name}  boss_total={row.BossDamage:N0}  boss_dps={row.BossDps:N0}  " +
                          $"hits={p.HitCount} (dot={p.DotHitCount})  crit={crit:P1}  back={ba:P1}");

            foreach (var s in p.Skills.Values.OrderByDescending(s => s.TotalDamage))
            {
                var info = _skillDb.Resolve((int)s.SkillCode);
                var skillName = info?.Name ?? "?";
                sb.AppendLine($"      {s.SkillCode,8}  {skillName,-20}  dmg={s.TotalDamage:N0}  " +
                              $"hits={s.HitCount}  crit={s.CritRate:P1}  avg={s.AverageDamage:N0}");
            }
        }

        sb.AppendLine();
        File.AppendAllText(_summaryLogPath, sb.ToString());
    }
}
