// FaceAffectMapper.cs
//
// Maps a FacialMetricMatrix expression classification to mutations of AffectState.
// This is the "face as emotional signal" loop — how B!'s internal state responds
// to what it observes on the user's face in real time.
//
// Lives in CircleAI.Companion (rather than CircleAI.Memory) because it bridges
// two modules: CircleAI.Tools (FacialMetricMatrix) and CircleAI.Memory (AffectState).
// Companion already references both; adding this here avoids a new project dependency.
//
// Mapping table (validated against fixtures/facex_biometric_vectors.json):
//   Happy     → Engagement += 0.03, Energy     += 0.02
//   Surprised → Curiosity  += 0.04
//   Confused  → Uncertainty += 0.05
//   Stressed  → Uncertainty += 0.08, Energy    -= 0.05
//   Angry     → Engagement -= 0.04, Rapport    -= 0.02
//   Neutral   → no change
//   Unknown   → no change (low-confidence detection, discarded upstream)
//
// All values are clamped to [0.0, 1.0] via Math.Min / Math.Max, consistent
// with AffectState.ApplyPositiveSignal / ApplyNegativeSignal conventions.
// Low-confidence detections (ConfidenceScore < 0.5) are discarded silently.

using System;
using CircleAI.Memory;
using CircleAI.Tools;

namespace CircleAI.Companion
{
    /// <summary>
    /// Maps <see cref="FacialMetricMatrix"/> expression observations to
    /// <see cref="AffectState"/> mutations using the existing five-axis model
    /// (Curiosity, Engagement, Uncertainty, Rapport, Energy).
    /// </summary>
    /// <remarks>
    /// All delta values are specified in
    /// <c>fixtures/facex_biometric_vectors.json</c> and must produce
    /// identical results across all 10 language runtimes within 1e-5 tolerance.
    /// </remarks>
    public static class FaceAffectMapper
    {
        /// <summary>
        /// Applies the facial expression observed in <paramref name="matrix"/>
        /// to <paramref name="affect"/>. Mutates <paramref name="affect"/> in place.
        /// A no-op when <paramref name="matrix"/> confidence is below 0.5 or
        /// the expression is <see cref="FaceExpressionClassification.Neutral"/>
        /// or <see cref="FaceExpressionClassification.Unknown"/>.
        /// </summary>
        /// <param name="matrix">
        /// Output of the facex pipeline for a single frame. Must not be null.
        /// </param>
        /// <param name="affect">
        /// The current AffectState for this user. Mutated in place. Must not be null.
        /// </param>
        public static void Apply(FacialMetricMatrix matrix, AffectState affect)
        {
            ArgumentNullException.ThrowIfNull(matrix);
            ArgumentNullException.ThrowIfNull(affect);

            // Discard low-confidence detections — do not let noisy frames
            // pollute the affect model.
            if (matrix.ConfidenceScore < 0.5f) return;

            switch (matrix.Expression)
            {
                case FaceExpressionClassification.Happy:
                    affect.Engagement = Math.Min(1f, affect.Engagement + 0.03f);
                    affect.Energy     = Math.Min(1f, affect.Energy     + 0.02f);
                    break;

                case FaceExpressionClassification.Surprised:
                    affect.Curiosity  = Math.Min(1f, affect.Curiosity  + 0.04f);
                    break;

                case FaceExpressionClassification.Confused:
                    affect.Uncertainty = Math.Min(1f, affect.Uncertainty + 0.05f);
                    break;

                case FaceExpressionClassification.Stressed:
                    affect.Uncertainty = Math.Min(1f, affect.Uncertainty + 0.08f);
                    affect.Energy      = Math.Max(0f, affect.Energy      - 0.05f);
                    break;

                case FaceExpressionClassification.Angry:
                    affect.Engagement = Math.Max(0f, affect.Engagement - 0.04f);
                    affect.Rapport    = Math.Max(0f, affect.Rapport    - 0.02f);
                    break;

                case FaceExpressionClassification.Neutral:
                case FaceExpressionClassification.Unknown:
                default:
                    // No affect change for neutral or unclassifiable expressions.
                    return;
            }

            affect.LastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }
}
