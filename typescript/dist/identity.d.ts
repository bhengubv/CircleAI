/** Verification tier for a CircleIdentity. */
export declare enum IdentityTier {
    Anonymous = "Anonymous",
    Pseudonymous = "Pseudonymous",
    Verified = "Verified"
}
/**
 * A Circle AI identity — the unified persona key that travels with the person.
 */
export interface CircleIdentity {
    /** Stable UUID — never changes. */
    readonly identityId: string;
    readonly displayName: string;
    readonly preferredLanguage: string | null;
    readonly tier: IdentityTier;
    readonly deviceIds: readonly string[];
    readonly createdAt: Date;
    readonly lastSeenAt: Date;
}
/**
 * A device registered to an identity.
 * platform is one of: "android" | "ios" | "windows" | "macos" | "linux" | "web" | "watch" | "iot"
 */
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
export declare abstract class IIdentityStore {
    abstract get(identityId: string): Promise<CircleIdentity | null>;
    abstract save(identity: CircleIdentity): Promise<void>;
    abstract getDevices(identityId: string): Promise<readonly RegisteredDevice[]>;
    abstract registerDevice(device: RegisteredDevice): Promise<void>;
    abstract getByDevice(deviceId: string): Promise<CircleIdentity | null>;
}
/**
 * Resolves the active identity for the current device/session.
 * Implementations may use local storage, biometrics, or mesh-distributed keys.
 */
export declare abstract class IIdentityProvider {
    abstract getCurrentIdentity(): Promise<CircleIdentity | null>;
    abstract isAuthenticated(): Promise<boolean>;
    abstract createIdentity(displayName: string, preferredLanguage?: string | null): Promise<CircleIdentity>;
}
/**
 * Biometric embedding template for a Circle AI identity.
 * The embeddingVector is the L2-normalised float array output of the facex
 * feature extractor for an enrolled face.
 *
 * IMPORTANT: Implementations of IBiometricStore MUST encrypt embeddingVector
 * at rest. Biometric templates are sensitive personal data under POPIA and GDPR.
 */
export interface BiometricProfile {
    /** The CircleIdentity.identityId this profile belongs to. */
    readonly identityId: string;
    /**
     * L2-normalised face embedding vector from the facex pipeline.
     * NOT a cryptographic hash — this is a fuzzy-matchable float array.
     * Typical dimensions: 128 (lightweight model) or 256 (full model).
     */
    readonly embeddingVector: number[];
    /**
     * Cosine similarity threshold at or above which a live embedding is
     * considered a positive match. Range [0.0, 1.0]. Default 0.85.
     */
    readonly matchThreshold: number;
    /** UTC timestamp when this template was enrolled. */
    readonly enrolledAt: Date;
    /** UTC timestamp of the most recent successful match, or undefined. */
    lastMatchAt?: Date;
    /** Dimension of embeddingVector (= embeddingVector.length). */
    readonly embeddingDimension: number;
}
/**
 * Persistent store for BiometricProfile records.
 * Implementations must encrypt embeddingVector at rest.
 */
export declare abstract class IBiometricStore {
    abstract get(identityId: string): Promise<BiometricProfile | null>;
    abstract save(profile: BiometricProfile): Promise<void>;
    abstract delete(identityId: string): Promise<void>;
    abstract exists(identityId: string): Promise<boolean>;
}
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
export declare function cosineSimilarity(a: number[], b: number[]): number;
/**
 * Returns true when liveEmbedding is a positive match for storedProfile —
 * i.e. the cosine similarity meets or exceeds profile.matchThreshold.
 */
export declare function isMatch(liveEmbedding: number[], profile: BiometricProfile): boolean;
