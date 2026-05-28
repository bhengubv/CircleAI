// BiometricMatcher.cs
//
// Pure cosine-similarity matching for face embedding vectors.
// No I/O, no native deps, no mutable state — safe to unit test
// and portable to all 10 target language runtimes.
//
// Cross-language determinism notes:
//   - Uses double accumulator to reduce float rounding drift across platforms.
//   - For L2-normalised vectors cosine similarity == dot product, so no
//     sqrt or division is needed (both are sources of platform divergence).
//   - Validated against fixtures/facex_biometric_vectors.json with 1e-5 tolerance.
//   - Do NOT use SIMD/NEON intrinsics here — FMADD vs separate mul+add
//     produces different rounding on ARM vs x86 and breaks fixture tests.

using System;

namespace CircleAI.Identity
{
    /// <summary>
    /// Deterministic cosine-similarity matching for L2-normalised face
    /// embedding vectors. All language ports must produce identical results
    /// within 1e-5 tolerance when validated against the fixture vectors.
    /// </summary>
    public static class BiometricMatcher
    {
        /// <summary>
        /// Computes the cosine similarity between two L2-normalised embedding
        /// vectors. Because both vectors are L2-normalised, this is equivalent
        /// to their dot product — no division or square root needed.
        /// </summary>
        /// <param name="a">First L2-normalised embedding. Must not be null.</param>
        /// <param name="b">Second L2-normalised embedding. Must match length of <paramref name="a"/>.</param>
        /// <returns>
        /// Cosine similarity in [-1.0, 1.0]. Values near 1.0 indicate the same
        /// face; values near 0.0 indicate unrelated faces.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="a"/> and <paramref name="b"/> have different lengths.
        /// </exception>
        public static float CosineSimilarity(float[] a, float[] b)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            if (a.Length != b.Length)
                throw new ArgumentException(
                    $"Embedding dimension mismatch: a={a.Length}, b={b.Length}. " +
                    "Both vectors must come from the same model.",
                    nameof(b));

            // Double accumulator for cross-platform reproducibility.
            double dot = 0.0;
            for (int i = 0; i < a.Length; i++)
                dot += (double)a[i] * b[i];

            return (float)dot;
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="liveEmbedding"/> is a
        /// positive match for <paramref name="storedProfile"/> — i.e. the
        /// cosine similarity meets or exceeds
        /// <see cref="BiometricProfile.MatchThreshold"/>.
        /// </summary>
        /// <param name="liveEmbedding">
        /// L2-normalised embedding of the live camera frame produced by facex.
        /// </param>
        /// <param name="storedProfile">
        /// The enrolled biometric profile to match against.
        /// </param>
        public static bool IsMatch(float[] liveEmbedding, BiometricProfile storedProfile)
        {
            ArgumentNullException.ThrowIfNull(liveEmbedding);
            ArgumentNullException.ThrowIfNull(storedProfile);

            var similarity = CosineSimilarity(liveEmbedding, storedProfile.EmbeddingVector);
            return similarity >= storedProfile.MatchThreshold;
        }
    }
}
