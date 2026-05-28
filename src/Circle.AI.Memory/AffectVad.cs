// AffectVad.cs
//
// Derived Valence / Arousal / Dominance view of AffectState.
//
// The Circle AI SDK uses a 5-dimensional affect model (curiosity, engagement,
// uncertainty, rapport, energy). Some downstream systems — including external
// affective-computing research tooling and HR/health analytics pipelines —
// expect Russell's PAD/VAD model. AffectVad is the DERIVED 3-dimensional view
// of the same underlying state; it does not replace AffectState.
//
// Derivation (all results clamped to [0.0, 1.0]):
//   Valence   = (engagement + rapport + (1 - uncertainty)) / 3
//   Arousal   = (energy * 2 + curiosity + uncertainty) / 4
//   Dominance = (engagement + (1 - uncertainty)) / 2
//
// These formulas are the cross-language fixture contract — see
// fixtures/affect_vad_derivation.json. Any change to the math must update
// every port and every fixture vector.

using System;

namespace Circle.AI.Memory;

/// <summary>
/// Derived Russell-PAD view of an <see cref="AffectState"/>.
/// All three dimensions are in [0.0, 1.0].
/// </summary>
/// <param name="Valence">
/// Pleasure ↔ displeasure axis. 1.0 = maximally pleasant, 0.0 = maximally unpleasant.
/// </param>
/// <param name="Arousal">
/// Activation ↔ deactivation axis. 1.0 = maximally aroused/alert,
/// 0.0 = maximally calm/dormant.
/// </param>
/// <param name="Dominance">
/// In-control ↔ submissive axis. 1.0 = maximally in control,
/// 0.0 = maximally submissive/overwhelmed.
/// </param>
public sealed record AffectVad(float Valence, float Arousal, float Dominance)
{
    /// <summary>
    /// Computes the VAD projection of an <see cref="AffectState"/> using the
    /// canonical fixture derivation. Output components are clamped to [0, 1].
    /// </summary>
    public static AffectVad From(AffectState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        float v = (state.Engagement + state.Rapport + (1f - state.Uncertainty)) / 3f;
        float a = (state.Energy * 2f + state.Curiosity + state.Uncertainty) / 4f;
        float d = (state.Engagement + (1f - state.Uncertainty)) / 2f;

        return new AffectVad(
            Valence  : Math.Clamp(v, 0f, 1f),
            Arousal  : Math.Clamp(a, 0f, 1f),
            Dominance: Math.Clamp(d, 0f, 1f));
    }
}

/// <summary>
/// Extension methods on <see cref="AffectState"/> for VAD projection.
/// </summary>
public static class AffectStateVadExtensions
{
    /// <summary>
    /// Projects this <see cref="AffectState"/> into the derived VAD view.
    /// </summary>
    public static AffectVad ToVad(this AffectState state) => AffectVad.From(state);
}
