namespace Aion2FunDps.Core.Sessions;

/// <summary>
/// Computes session "trust score" from observed leak indicators.
/// Higher = more accurate measurement; lower = more noise/loss.
///
/// Differentiator vs other meters: most silently leak. We surface it.
/// </summary>
public sealed class AccuracyEstimator
{
    public long DroppedPackets { get; set; }
    public long MalformedFrames { get; set; }
    public long UnknownOpcodes { get; set; }
    public long UnmappedSummonHits { get; set; }

    /// <summary>
    /// Ratio of damage we missed to focus target: max(0, hpDelta - sumDamage) / hpDelta.
    /// 0 = perfect, 1 = totally missed everything.
    /// </summary>
    public double HpDamageDrift { get; set; }

    /// <summary>Final confidence score 0..1 (higher is better). Recomputed via Recompute().</summary>
    public double ConfidenceScore { get; private set; } = 1.0;

    public void Recompute()
    {
        double score = 1.0;

        // Drops: -1% per 100, capped at -20%
        score -= Math.Min(0.20, DroppedPackets / 100.0 * 0.01);

        // Malformed frames: -1% per 50, capped at -10%
        score -= Math.Min(0.10, MalformedFrames / 50.0 * 0.01);

        // Unknown opcodes: -1% per 200, capped at -5%
        // (Most game packets are non-DPS — many unknown is expected)
        score -= Math.Min(0.05, UnknownOpcodes / 200.0 * 0.01);

        // Unmapped summon hits: -2% each, capped at -10%
        score -= Math.Min(0.10, UnmappedSummonHits * 0.02);

        // HP-damage drift: tiered
        if (HpDamageDrift > 0.20) score -= 0.30;
        else if (HpDamageDrift > 0.10) score -= 0.20;
        else if (HpDamageDrift > 0.05) score -= 0.10;
        else if (HpDamageDrift > 0.02) score -= 0.05;

        ConfidenceScore = Math.Max(0, score);
    }

    public string StatusEmoji =>
        ConfidenceScore >= 0.90 ? "✅"
        : ConfidenceScore >= 0.70 ? "⚠️"
        : "❌";

    public string Tier =>
        ConfidenceScore >= 0.95 ? "Excellent"
        : ConfidenceScore >= 0.90 ? "Good"
        : ConfidenceScore >= 0.70 ? "Caution"
        : "Poor";

    public IEnumerable<string> Issues()
    {
        if (DroppedPackets > 0)        yield return $"드랍 {DroppedPackets}";
        if (MalformedFrames > 0)       yield return $"malformed {MalformedFrames}";
        if (UnknownOpcodes > 200)      yield return $"unknown {UnknownOpcodes}";
        if (UnmappedSummonHits > 0)    yield return $"미매핑 소환수 {UnmappedSummonHits}";
        if (HpDamageDrift > 0.02)      yield return $"HP 드리프트 {HpDamageDrift:P1}";
    }
}
