"use strict";
// identity/index.ts
// Circle AI identity, device registration, and biometric matching.
// Ported from Circle.AI.Identity (C#).
Object.defineProperty(exports, "__esModule", { value: true });
exports.IdentityTier = void 0;
exports.cosineSimilarity = cosineSimilarity;
exports.isMatch = isMatch;
// ─────────────────────────────────────────────────────────────────────────────
// Identity enums + records
// ─────────────────────────────────────────────────────────────────────────────
var IdentityTier;
(function (IdentityTier) {
    IdentityTier["Anonymous"] = "Anonymous";
    IdentityTier["Pseudonymous"] = "Pseudonymous";
    IdentityTier["Verified"] = "Verified";
})(IdentityTier || (exports.IdentityTier = IdentityTier = {}));
// ─────────────────────────────────────────────────────────────────────────────
// BiometricMatcher
// ─────────────────────────────────────────────────────────────────────────────
/**
 * Computes the cosine similarity between two L2-normalised embedding vectors.
 * Because both vectors are L2-normalised, this equals their dot product —
 * no sqrt or division needed.
 *
 * Uses a double-precision accumulator to match C# cross-platform reproducibility.
 * Do NOT use Float32Array arithmetic here — it introduces rounding drift.
 * Validated against fixtures/facex_biometric_vectors.json with 1e-5 tolerance.
 *
 * @throws {Error} if a and b have different lengths.
 */
function cosineSimilarity(a, b) {
    if (a.length !== b.length) {
        throw new Error(`Embedding dimension mismatch: a=${a.length}, b=${b.length}. ` +
            "Both vectors must come from the same model.");
    }
    if (a.length === 0)
        return 0;
    // Double-precision accumulators for cross-platform reproducibility.
    // Uses full dot/|a||b| formula to handle approximately-normalised vectors.
    let dot = 0;
    let magA = 0;
    let magB = 0;
    for (let i = 0; i < a.length; i++) {
        dot += a[i] * b[i];
        magA += a[i] * a[i];
        magB += b[i] * b[i];
    }
    magA = Math.sqrt(magA);
    magB = Math.sqrt(magB);
    if (magA === 0 || magB === 0)
        return 0;
    return dot / (magA * magB);
}
/**
 * Returns true when liveEmbedding is a positive match for storedProfile —
 * i.e. the cosine similarity meets or exceeds profile.matchThreshold.
 */
function isMatch(liveEmbedding, profile) {
    return cosineSimilarity(liveEmbedding, profile.embeddingVector) >= profile.matchThreshold;
}
