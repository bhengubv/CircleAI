"use strict";
// identity.ts
//
// Circle AI identity layer.
// A Circle identity is the unified persona key that travels with the person.
// Phone → Watch → Desktop → Smart Speaker → Car: same identity, same memory.
Object.defineProperty(exports, "__esModule", { value: true });
exports.IBiometricStore = exports.IIdentityProvider = exports.IIdentityStore = exports.IdentityTier = void 0;
exports.cosineSimilarity = cosineSimilarity;
exports.isMatch = isMatch;
/** Verification tier for a CircleIdentity. */
var IdentityTier;
(function (IdentityTier) {
    IdentityTier["Anonymous"] = "Anonymous";
    IdentityTier["Pseudonymous"] = "Pseudonymous";
    IdentityTier["Verified"] = "Verified";
})(IdentityTier || (exports.IdentityTier = IdentityTier = {}));
/** Persistent store for Circle AI identities and device registrations. */
class IIdentityStore {
}
exports.IIdentityStore = IIdentityStore;
/**
 * Resolves the active identity for the current device/session.
 * Implementations may use local storage, biometrics, or mesh-distributed keys.
 */
class IIdentityProvider {
}
exports.IIdentityProvider = IIdentityProvider;
/**
 * Persistent store for BiometricProfile records.
 * Implementations must encrypt embeddingVector at rest.
 */
class IBiometricStore {
}
exports.IBiometricStore = IBiometricStore;
// ---------------------------------------------------------------------------
// BiometricMatcher
// ---------------------------------------------------------------------------
/**
 * Computes the cosine similarity between two L2-normalised embedding vectors.
 * Because both vectors are L2-normalised, this equals their dot product —
 * no sqrt or division needed.
 *
 * Uses a double-precision accumulator to match C# cross-platform reproducibility.
 * Do NOT use Float32Array arithmetic — it introduces rounding drift.
 * Validated against fixtures/facex_biometric_vectors.json with 1e-5 tolerance.
 *
 * @throws {Error} if a and b have different lengths.
 */
function cosineSimilarity(a, b) {
    if (a.length !== b.length) {
        throw new Error(`Embedding dimension mismatch: a=${a.length}, b=${b.length}. ` +
            'Both vectors must come from the same model.');
    }
    if (a.length === 0)
        return 0;
    // Double-precision accumulators for cross-platform reproducibility.
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
