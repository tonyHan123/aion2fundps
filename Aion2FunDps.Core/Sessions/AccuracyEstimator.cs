namespace Aion2FunDps.Core.Sessions;

/// <summary>
/// Computes session "trust score" from observed leak indicators.
///
/// Design principle: <see cref="HpDamageDrift"/> is the ground truth. When a boss
/// is being hit and we can compare its HP delta vs. our recorded damage, that
/// number IS the leak — and other indicators (drops, malformed, unknown opcodes)
/// are noisy proxies that should not contradict it. So:
///
///   - If drift is measurable → score is determined by drift alone.
///   - If drift is not yet measurable (no boss damaged yet) → fall back to
///     a soft penalty on capture-layer indicators.
///
/// Capture-layer counters are session-scoped: <see cref="RebaselineToCurrent"/>
/// is called on session reset so a long play session doesn't drag the score down
/// from old, irrelevant accumulations.
/// </summary>
public sealed class AccuracyEstimator
{
    // Cumulative counters fed in from upstream subsystems each refresh.
    public long DroppedPackets { get; set; }
    public long MalformedFrames { get; set; }
    public long UnknownOpcodes { get; set; }
    public long UnmappedSummonHits { get; set; }

    // Baselines captured on the most recent session reset. Score uses
    // (current - baseline) so per-session noise is what's penalised.
    private long _droppedBaseline;
    private long _malformedBaseline;
    private long _unknownBaseline;
    private long _unmappedSummonBaseline;

    public long SessionDrops          => Math.Max(0, DroppedPackets - _droppedBaseline);
    public long SessionMalformed      => Math.Max(0, MalformedFrames - _malformedBaseline);
    public long SessionUnknown        => Math.Max(0, UnknownOpcodes - _unknownBaseline);
    public long SessionUnmappedSummon => Math.Max(0, UnmappedSummonHits - _unmappedSummonBaseline);

    /// <summary>
    /// Ratio of damage we missed to focus target: max(0, hpDelta - sumDamage) / hpDelta.
    /// 0 = perfect, 1 = totally missed everything.
    /// </summary>
    public double HpDamageDrift { get; set; }

    /// <summary>
    /// True when we have credible drift data (boss focused AND HP has actually moved
    /// AND we recorded some damage to it). When false the score falls back to capture
    /// indicators because drift can't be evaluated yet.
    /// </summary>
    public bool HasDriftSignal { get; set; }

    /// <summary>Final confidence score 0..1 (higher is better). Recomputed via Recompute().</summary>
    public double ConfidenceScore { get; private set; } = 1.0;

    /// <summary>
    /// Snapshot the current cumulative counters as the new session baseline.
    /// Call this on session reset so previously-accumulated noise stops counting.
    /// </summary>
    public void RebaselineToCurrent()
    {
        _droppedBaseline = DroppedPackets;
        _malformedBaseline = MalformedFrames;
        _unknownBaseline = UnknownOpcodes;
        _unmappedSummonBaseline = UnmappedSummonHits;
        HpDamageDrift = 0;
        HasDriftSignal = false;
    }

    public void Recompute()
    {
        // Ground truth available — drift IS the score.
        if (HasDriftSignal)
        {
            ConfidenceScore = HpDamageDrift switch
            {
                <= 0.02 => 1.00,   // ≤2% drift: verified accurate
                <= 0.05 => 0.95,
                <= 0.10 => 0.85,
                <= 0.20 => 0.70,
                _       => 0.50,   // >20% drift: serious leak
            };
            return;
        }

        // No drift signal yet (no boss damage observed this session) — fall back
        // to soft penalties on capture-quality indicators. These are MUCH milder
        // than the old formula because they're noisy proxies, not real leak.
        double score = 1.0;

        // Real packet drops: still concerning even without drift to verify
        score -= Math.Min(0.10, SessionDrops / 100.0 * 0.01);          // -1%/100, max -10%

        // Malformed: mostly benign LZ4 truncations etc. — minimal weight
        score -= Math.Min(0.05, SessionMalformed / 500.0 * 0.01);      // -1%/500, max -5%

        // Unknown opcodes: mostly non-DPS noise — almost no weight
        score -= Math.Min(0.02, SessionUnknown / 2000.0 * 0.01);       // -1%/2000, max -2%

        // Unmapped summon hits: real attribution leak
        score -= Math.Min(0.05, SessionUnmappedSummon * 0.01);         // -1% each, max -5%

        ConfidenceScore = Math.Max(0, score);
    }

    public string StatusEmoji =>
        ConfidenceScore >= 0.95 ? "✅"
        : ConfidenceScore >= 0.80 ? "⚠️"
        : "❌";

    public string Tier =>
        ConfidenceScore >= 0.98 ? "Excellent"
        : ConfidenceScore >= 0.90 ? "Good"
        : ConfidenceScore >= 0.75 ? "Caution"
        : "Poor";

    public IEnumerable<string> Issues()
    {
        if (HasDriftSignal && HpDamageDrift > 0.02)
            yield return $"HP 누수 {HpDamageDrift:P1}";
        if (SessionDrops > 0)
            yield return $"드랍 {SessionDrops}";
        if (SessionMalformed > 200)
            yield return $"malformed {SessionMalformed}";
        if (SessionUnknown > 2000)
            yield return $"unknown {SessionUnknown}";
        if (SessionUnmappedSummon > 0)
            yield return $"미매핑 소환수 {SessionUnmappedSummon}";
    }
}
