// BiometricMatcherTest.ets — Cross-language BiometricMatcher verification (ArkTS)
// All vectors from fixtures/facex_biometric_vectors.json cosine_similarity_vectors.
// Each computed value must match within the fixture's tolerance (1e-4 or 1e-5).
// Uses if/throw assertion pattern (no external test framework).

import { cosineSimilarity, isMatch, BiometricProfile } from '../src/main/ets/identity/index';

// ---------------------------------------------------------------------------
// Assertion helper
// ---------------------------------------------------------------------------

function assertApprox(label: string, actual: number, expected: number, tol: number = 1e-5): void {
  const diff: number = Math.abs(actual - expected);
  if (diff > tol) {
    throw new Error(`FAIL ${label}: expected ${expected}, got ${actual}, diff=${diff} (tol=${tol})`);
  }
}

function assertTrue(label: string, condition: boolean): void {
  if (!condition) throw new Error(`FAIL ${label}: condition was false`);
}

function assertFalse(label: string, condition: boolean): void {
  if (condition) throw new Error(`FAIL ${label}: condition was true (expected false)`);
}

// ---------------------------------------------------------------------------
// Vector: identical_unit_vectors_2d
// cos_sim([0.6, 0.8], [0.6, 0.8]) = 1.0
// ---------------------------------------------------------------------------

((): void => {
  const a: number[] = [0.6, 0.8];
  const b: number[] = [0.6, 0.8];
  const sim: number = cosineSimilarity(a, b);
  assertApprox('identical_2d', sim, 1.0, 1e-5);
  console.log('PASS: identical_unit_vectors_2d');
})();

// ---------------------------------------------------------------------------
// Vector: orthogonal_vectors_2d
// cos_sim([1, 0], [0, 1]) = 0.0
// ---------------------------------------------------------------------------

((): void => {
  const a: number[] = [1.0, 0.0];
  const b: number[] = [0.0, 1.0];
  const sim: number = cosineSimilarity(a, b);
  assertApprox('orthogonal_2d', sim, 0.0, 1e-5);
  console.log('PASS: orthogonal_vectors_2d');
})();

// ---------------------------------------------------------------------------
// Vector: opposite_vectors_2d
// cos_sim([1, 0], [-1, 0]) = -1.0
// ---------------------------------------------------------------------------

((): void => {
  const a: number[] = [1.0, 0.0];
  const b: number[] = [-1.0, 0.0];
  const sim: number = cosineSimilarity(a, b);
  assertApprox('opposite_2d', sim, -1.0, 1e-5);
  console.log('PASS: opposite_vectors_2d');
})();

// ---------------------------------------------------------------------------
// Vector: same_face_high_similarity_4d — fixture expected 0.999794, tol 1e-4
// ---------------------------------------------------------------------------

((): void => {
  const a: number[] = [0.5257, 0.7236, 0.2425, 0.3780];
  const b: number[] = [0.5133, 0.7340, 0.2511, 0.3692];
  const sim: number = cosineSimilarity(a, b);
  assertApprox('same_face_4d', sim, 0.999794, 1e-4);
  const profile: BiometricProfile = {
    identityId: 'test-same-face',
    embeddingVector: b,
    matchThreshold: 0.85,
    enrolledAt: Date.now(),
  };
  assertTrue('same_face_is_match', isMatch(a, profile));
  console.log('PASS: same_face_high_similarity_4d');
})();

// ---------------------------------------------------------------------------
// Vector: different_face_low_similarity_4d — fixture expected 0.311911, tol 1e-4
// ---------------------------------------------------------------------------

((): void => {
  const a: number[] = [0.5257, 0.7236, 0.2425, 0.3780];
  const b: number[] = [-0.3015, 0.6547, 0.5893, -0.3812];
  const sim: number = cosineSimilarity(a, b);
  assertApprox('different_face_4d', sim, 0.311911, 1e-4);
  // Behavioral: low (< 0.5) for different-face vectors
  assertTrue('different_face_4d_sim_below_0_5', sim < 0.5);
  const profile: BiometricProfile = {
    identityId: 'test-different-face',
    embeddingVector: b,
    matchThreshold: 0.85,
    enrolledAt: Date.now(),
  };
  assertFalse('different_face_not_match', isMatch(a, profile));
  console.log('PASS: different_face_low_similarity_4d');
})();

// ---------------------------------------------------------------------------
// Vector: marginal_match_exactly_at_threshold
// Identical vectors => sim = 1.0 >= 0.85 → isMatch = true
// ---------------------------------------------------------------------------

((): void => {
  const a: number[] = [0.7071, 0.7071];
  const b: number[] = [0.7071, 0.7071];
  const sim: number = cosineSimilarity(a, b);
  assertApprox('marginal_sim', sim, 1.0, 1e-5);
  const profile: BiometricProfile = {
    identityId: 'test-marginal',
    embeddingVector: b,
    matchThreshold: 0.85,
    enrolledAt: Date.now(),
  };
  assertTrue('marginal_is_match', isMatch(a, profile));
  console.log('PASS: marginal_match_exactly_at_threshold');
})();

// ---------------------------------------------------------------------------
// Edge cases
// ---------------------------------------------------------------------------

((): void => {
  // Empty arrays → 0
  const sim: number = cosineSimilarity([], []);
  assertApprox('empty_vectors', sim, 0, 1e-5);
  console.log('PASS: empty_arrays_return_0');
})();

((): void => {
  // Different-length vectors are REFUSED, not scored.
  //
  // A similarity for embeddings of different dimensions is meaningless, and
  // silently returning one is how a false match happens — so this port throws
  // rather than answering. (Note for anyone reconciling the ports: Python's
  // cosine_similarity zips the two lists and returns 0.894 here instead of
  // refusing.)
  let refused = false;
  try {
    cosineSimilarity([1.0], [1.0, 0.5]);
  } catch (e) {
    refused = true;
  }
  assertTrue('mismatched_length_refused', refused);
  console.log('PASS: mismatched_length_is_refused');
})();

((): void => {
  // Zero vector → 0 (magA < 1e-10)
  const sim: number = cosineSimilarity([0.0, 0.0], [1.0, 0.0]);
  assertApprox('zero_vector', sim, 0, 1e-5);
  console.log('PASS: zero_vector_return_0');
})();

((): void => {
  // isMatch at exactly the threshold value — uses >= so must be true
  const profile: BiometricProfile = {
    identityId: 'test-at-threshold',
    embeddingVector: [1.0, 0.0],
    matchThreshold: 1.0,
    enrolledAt: Date.now(),
  };
  assertTrue('exact_threshold_match', isMatch([1.0, 0.0], profile));
  console.log('PASS: is_match_at_exact_threshold');
})();

console.log('BiometricMatcherTest: ALL PASS');
