// BiometricMatcher.cs
//
// Pure cosine-similarity matching for face embedding vectors.
// No I/O, no native deps, no mutable state — safe to unit test
// and portable to all 10 target language runtimes.
//
// Cross-language determinism notes:
//   - Uses double accumulators to reduce float rounding drift across platforms.
//   - Computes the FULL cosine — dot / (|a| * |b|). This previously returned the
//     bare dot product on the grounds that cosine == dot for L2-normalised
//     vectors. True, but L2-normalisation is a precondition this code documents
//     in four places and enforces in none: EmbeddingVector is a plain float[]
//     that anything can fill. An un-normalised vector then scored outside
//     [-1, 1] and cleared any threshold, and the fixture's own vectors are not
//     normalised (|a| = 1.000825), so C# disagreed with the eight other ports
//     on two of the six rows. Normalised input is unaffected: dividing by
//     1.0 changes nothing.
//   - Validated against fixtures/facex_biometric_vectors.json with each entry's
//     stated tolerance — see BiometricMatcherFixtureTests. That claim stood in
//     this header for a long time while NO C# test loaded the file; the test
//     now exists, and it is what makes the claim checkable.
//   - Do NOT use SIMD/NEON intrinsics here — FMADD vs separate mul+add
//     produces different rounding on ARM vs x86 and breaks fixture tests.
//     (This is why the matcher does not call VectorMath.CosineSimilarity,
//     which is hardware-accelerated by design.)

using System;

namespace CircleAI.Identity
{
    /// <summary>
    /// Deterministic cosine-similarity matching for face embedding vectors.
    /// All language ports must produce identical results within the tolerance
    /// stated by each fixture vector.
    /// </summary>
    public static class BiometricMatcher
    {
        /// <summary>
        /// Computes the cosine similarity between two embedding vectors as
        /// <c>dot / (|a| * |b|)</c>.
        /// </summary>
        /// <param name="a">First embedding. Must not be null.</param>
        /// <param name="b">Second embedding. Must match length of <paramref name="a"/>.</param>
        /// <returns>
        /// Cosine similarity, clamped to [-1.0, 1.0]. Values near 1.0 indicate
        /// the same face; values near 0.0 indicate unrelated faces. Returns 0.0
        /// when either vector is empty or of near-zero magnitude.
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

            // Double accumulators for cross-platform reproducibility.
            double dot = 0.0;
            double magA = 0.0;
            double magB = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                double ai = a[i];
                double bi = b[i];
                dot  += ai * bi;
                magA += ai * ai;
                magB += bi * bi;
            }

            magA = Math.Sqrt(magA);
            magB = Math.Sqrt(magB);

            // Near-zero rather than exactly zero, matching every other port: a
            // vector of 1e-30s has a non-zero magnitude and divides into noise.
            if (magA < 1e-10 || magB < 1e-10)
                return 0.0f;

            return (float)Math.Clamp(dot / (magA * magB), -1.0, 1.0);
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="liveEmbedding"/> is a
        /// positive match for <paramref name="storedProfile"/> — i.e. the
        /// cosine similarity meets or exceeds
        /// <see cref="BiometricProfile.MatchThreshold"/>.
        /// </summary>
        /// <param name="liveEmbedding">
        /// Embedding of the live camera frame produced by facex.
        /// </param>
        /// <param name="storedProfile">
        /// The enrolled biometric profile to match against.
        /// </param>
        /// <exception cref="ArgumentException">
        /// The live embedding and the enrolled profile have different
        /// dimensions — a mismatched model rather than a failed match, and
        /// surfaced rather than reported as a quiet <c>false</c>.
        /// </exception>
        public static bool IsMatch(float[] liveEmbedding, BiometricProfile storedProfile)
        {
            ArgumentNullException.ThrowIfNull(liveEmbedding);
            ArgumentNullException.ThrowIfNull(storedProfile);

            var similarity = CosineSimilarity(liveEmbedding, storedProfile.EmbeddingVector);
            return similarity >= storedProfile.MatchThreshold;
        }
    }
}
