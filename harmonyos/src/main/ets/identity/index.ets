// identity/index.ets — Circle AI identity layer
// ArkTS port: no `any`, enum-based IdentityTier, explicit types.

/**
 * Verification tier for a CircleIdentity.
 * Higher tiers unlock higher transaction limits (per FICA/SARB tiers).
 */
export enum IdentityTier {
  Anonymous    = 0,
  Pseudonymous = 1,
  Verified     = 2,
}

/**
 * A Circle AI identity — the unified persona key that travels with the person.
 */
export interface CircleIdentity {
  /** Stable UUID — never changes. */
  identityId: string;
  displayName: string;
  preferredLanguage?: string;   // BCP-47, optional
  tier: IdentityTier;
  deviceIds: string[];
  createdAt: number;            // unix ms
  lastSeenAt: number;           // unix ms
}

/**
 * A device registered to a CircleIdentity.
 * platform is one of: "android" | "ios" | "windows" | "macos" | "linux" |
 *                     "web" | "watch" | "iot" | "harmonyos"
 */
export interface RegisteredDevice {
  deviceId: string;
  identityId: string;
  platform: string;
  deviceName?: string;
  registeredAt: number;   // unix ms
  lastActiveAt: number;   // unix ms
}

/**
 * L2-normalised biometric embedding profile for a given identity.
 * CRITICAL: embeddingVector must be L2-normalised floats.
 * matchThreshold default 0.85.
 */
export interface BiometricProfile {
  identityId: string;
  embeddingVector: number[];   // L2-normalised floats
  matchThreshold: number;      // default 0.85
  enrolledAt: number;          // unix ms
  lastMatchAt?: number;        // unix ms, optional
}

/**
 * BiometricMatcher — CRITICAL:
 * All loops must be plain scalar number arithmetic.
 * No SIMD, no Float32Array tricks.
 * This ensures cross-platform reproducibility within 1e-5.
 *
 * Formula: cosine_similarity(a, b) = dot(a,b) / (||a|| * ||b||)
 * Result clamped to [-1, 1].
 */
export function cosineSimilarity(a: number[], b: number[]): number {
  if (a.length !== b.length || a.length === 0) return 0;
  let dot: number  = 0;
  let magA: number = 0;
  let magB: number = 0;
  for (let i: number = 0; i < a.length; i++) {
    dot  += a[i] * b[i];
    magA += a[i] * a[i];
    magB += b[i] * b[i];
  }
  magA = Math.sqrt(magA);
  magB = Math.sqrt(magB);
  if (magA < 1e-10 || magB < 1e-10) return 0;
  return Math.max(-1, Math.min(1, dot / (magA * magB)));
}

/**
 * Returns true when the candidate embedding matches the profile.
 * Uses >= comparison so a similarity exactly at threshold is a match.
 */
export function isMatch(candidate: number[], profile: BiometricProfile): boolean {
  return cosineSimilarity(candidate, profile.embeddingVector) >= profile.matchThreshold;
}

/** Persistent store for biometric profiles. Supports RTBF (right to be forgotten). */
export interface IBiometricStore {
  get(identityId: string): Promise<BiometricProfile | null>;
  save(profile: BiometricProfile): Promise<void>;
  delete(identityId: string): Promise<void>;    // RTBF support
  exists(identityId: string): Promise<boolean>;
}

/** Persistent store for Circle AI identities and device registrations. */
export interface IIdentityStore {
  get(identityId: string): Promise<CircleIdentity | null>;
  save(identity: CircleIdentity): Promise<void>;
  getDevices(identityId: string): Promise<RegisteredDevice[]>;
  registerDevice(device: RegisteredDevice): Promise<void>;
  getByDevice(deviceId: string): Promise<CircleIdentity | null>;
}
