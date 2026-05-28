// identity/index.ts
// Circle AI identity, device registration, and biometric matching.
// Ported from Circle.AI.Identity (C#).

// ─────────────────────────────────────────────────────────────────────────────
// Identity enums + records
// ─────────────────────────────────────────────────────────────────────────────

export enum IdentityTier {
  Anonymous = "Anonymous",
  Pseudonymous = "Pseudonymous",
  Verified = "Verified",
}

/**
 * A Circle AI identity — the unified persona key that travels with the person.
 * Phone → Watch → Desktop → Smart Speaker → Car: same identity, same memory.
 */
export interface CircleIdentity {
  readonly identityId: string; // stable UUID — never changes
  readonly displayName: string;
  readonly preferredLanguage: string | null;
  readonly tier: IdentityTier;
  readonly deviceIds: readonly string[];
  readonly createdAt: Date;
  readonly lastSeenAt: Date;
}

/** A device registered to an identity. */
export interface RegisteredDevice {
  readonly deviceId: string;
  readonly identityId: string;
  /** "android" | "ios" | "windows" | "macos" | "linux" | "web" | "watch" | "iot" */
  readonly platform: string;
  readonly deviceName: string | null;
  readonly registeredAt: Date;
  readonly lastActiveAt: Date;
}

/** Persistent store for Circle AI identities and device registrations. */
export interface IIdentityStore {
  getAsync(identityId: string): Promise<CircleIdentity | null>;
  saveAsync(identity: CircleIdentity): Promise<void>;
  getDevicesAsync(identityId: string): Promise<readonly RegisteredDevice[]>;
  registerDeviceAsync(device: RegisteredDevice): Promise<void>;
  getByDeviceAsync(deviceId: string): Promise<CircleIdentity | null>;
}

// ─────────────────────────────────────────────────────────────────────────────
// BiometricProfile
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Biometric embedding template for a Circle AI identity.
 * The embeddingVector is the L2-normalised float array output of the facex
 * feature extractor for an enrolled face.
 * Matching is performed by isMatch().
 *
 * IMPORTANT: Implementations of IBiometricStore MUST encrypt embeddingVector
 * at rest. Biometric templates are sensitive personal data under POPIA and GDPR.
 */
export interface BiometricProfile {
  readonly identityId: string;
  /**
   * L2-normalised face embedding vector from the facex pipeline.
   * NOT a cryptographic hash — this is a fuzzy-matchable float array.
   */
  readonly embeddingVector: number[];
  /**
   * Cosine similarity threshold at or above which a live embedding is
   * considered a positive match. Range [0.0, 1.0]. Default 0.85.
   */
  readonly matchThreshold: number;
  readonly enrolledAt: Date;
  lastMatchAt?: Date;
  /** Dimension of embeddingVector (= embeddingVector.length). */
  readonly embeddingDimension: number;
}

/**
 * Persistent store for BiometricProfile records.
 * Implementations must encrypt embeddingVector at rest.
 */
export interface IBiometricStore {
  /** Load the biometric profile for identityId. Returns null if not enrolled. */
  getAsync(identityId: string): Promise<BiometricProfile | null>;
  /** Persist (or overwrite) a biometric profile atomically. */
  saveAsync(profile: BiometricProfile): Promise<void>;
  /**
   * Permanently delete the biometric profile. Must irrecoverably destroy the
   * stored embedding to satisfy right-to-be-forgotten obligations.
   * No-op if no profile exists.
   */
  deleteAsync(identityId: string): Promise<void>;
  /** Returns true if an enrolled biometric profile exists for identityId. */
  existsAsync(identityId: string): Promise<boolean>;
}

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
export function cosineSimilarity(a: number[], b: number[]): number {
  if (a.length !== b.length) {
    throw new Error(
      `Embedding dimension mismatch: a=${a.length}, b=${b.length}. ` +
      "Both vectors must come from the same model."
    );
  }
  if (a.length === 0) return 0;
  // Double-precision accumulators for cross-platform reproducibility.
  // Uses full dot/|a||b| formula to handle approximately-normalised vectors.
  let dot = 0;
  let magA = 0;
  let magB = 0;
  for (let i = 0; i < a.length; i++) {
    dot  += a[i] * b[i];
    magA += a[i] * a[i];
    magB += b[i] * b[i];
  }
  magA = Math.sqrt(magA);
  magB = Math.sqrt(magB);
  if (magA === 0 || magB === 0) return 0;
  return dot / (magA * magB);
}

/**
 * Returns true when liveEmbedding is a positive match for storedProfile —
 * i.e. the cosine similarity meets or exceeds profile.matchThreshold.
 */
export function isMatch(liveEmbedding: number[], profile: BiometricProfile): boolean {
  return cosineSimilarity(liveEmbedding, profile.embeddingVector) >= profile.matchThreshold;
}
