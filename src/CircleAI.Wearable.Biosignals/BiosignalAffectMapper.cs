// BiosignalAffectMapper.cs
//
// Deterministic projection of biosignal samples onto AffectState mutations.
// Pure function on (sample, state). No persistence. No side effects beyond the
// state mutation. Same rules can be ported to Rust/Go/Python/Swift/TS.

using CircleAI.Memory;

namespace CircleAI.Wearable.Biosignals;

/// <summary>
/// Maps biosignal samples to <see cref="AffectState"/> mutations using
/// deterministic, fixture-validated rules.
/// </summary>
/// <remarks>
/// Rule sheet (all mutations clamped to [0, 1]):
/// <list type="bullet">
///   <item>HeartRate &gt; 130 bpm (conf ≥ 0.5): Energy += 0.10, Uncertainty += 0.05.</item>
///   <item>HeartRate &gt; 100 bpm (conf ≥ 0.5): Energy += 0.05.</item>
///   <item>HeartRate &lt; 50 bpm (conf ≥ 0.5): Energy -= 0.05.</item>
///   <item>HRV &lt; 20 ms (conf ≥ 0.5): Uncertainty += 0.05, Rapport -= 0.02.</item>
///   <item>HRV &gt; 60 ms (conf ≥ 0.5): Engagement += 0.02.</item>
///   <item>SpO2 &lt; 90 % (conf ≥ 0.5): Uncertainty += 0.10.</item>
///   <item>SleepStage 2 or 3 (deep / REM): no mutation — user is not interacting.</item>
///   <item>Confidence &lt; 0.5 on any signal: no mutation.</item>
/// </list>
/// </remarks>
public static class BiosignalAffectMapper
{
    private const float MinConfidence = 0.5f;

    /// <summary>
    /// Applies the rule for <paramref name="sample"/> to <paramref name="affect"/>.
    /// Mutates <paramref name="affect"/> in place. Safe to call repeatedly — all
    /// resulting field values are clamped to [0, 1].
    /// </summary>
    /// <param name="sample">The biosignal sample.</param>
    /// <param name="affect">The affect state to mutate.</param>
    public static void Apply(BiosignalSample sample, AffectState affect)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(affect);

        // Confidence gate — low-confidence samples never mutate state.
        if (sample.Confidence < MinConfidence) return;

        switch (sample.Kind)
        {
            case BiosignalKind.HeartRate:
                ApplyHeartRate(sample.Value, affect);
                break;

            case BiosignalKind.HeartRateVariability:
                ApplyHrv(sample.Value, affect);
                break;

            case BiosignalKind.OxygenSaturation:
                ApplySpO2(sample.Value, affect);
                break;

            case BiosignalKind.SleepStage:
                // Deep (2) / REM (3) — user is not interacting; do nothing.
                // Awake (0) / Light (1) — also no mutation; sleep itself is not affect.
                break;

            // The remaining kinds (Accelerometer, Temperature, Steps, GSR, Unknown)
            // do not currently drive affect — left for future rule additions.
            default:
                break;
        }

        affect.LastUpdatedUtc = DateTimeOffset.UtcNow;
    }

    private static void ApplyHeartRate(float bpm, AffectState a)
    {
        if (bpm > 130f)
        {
            a.Energy      = Clamp01(a.Energy      + 0.10f);
            a.Uncertainty = Clamp01(a.Uncertainty + 0.05f);
        }
        else if (bpm > 100f)
        {
            a.Energy = Clamp01(a.Energy + 0.05f);
        }
        else if (bpm < 50f)
        {
            a.Energy = Clamp01(a.Energy - 0.05f);
        }
    }

    private static void ApplyHrv(float rmssdMs, AffectState a)
    {
        if (rmssdMs < 20f)
        {
            a.Uncertainty = Clamp01(a.Uncertainty + 0.05f);
            a.Rapport     = Clamp01(a.Rapport     - 0.02f);
        }
        else if (rmssdMs > 60f)
        {
            a.Engagement = Clamp01(a.Engagement + 0.02f);
        }
    }

    private static void ApplySpO2(float percent, AffectState a)
    {
        if (percent < 90f)
        {
            a.Uncertainty = Clamp01(a.Uncertainty + 0.10f);
        }
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);
}
