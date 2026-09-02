// Identity.swift
//
// IdentityTier, CircleIdentity, RegisteredDevice, and their store/provider
// protocols. A Circle AI identity travels with the person across every surface.

import Foundation

// MARK: - IdentityTier

/// Trust level of a CircleIdentity.
public enum IdentityTier: String, Sendable, CaseIterable {
    /// No verification — minimal trust.
    case anonymous
    /// Device-bound pseudonym — medium trust.
    case pseudonymous
    /// Phone/biometric verified — full trust.
    case verified
}

// MARK: - CircleIdentity

/// A Circle AI identity — the unified persona key that travels with the person.
/// Phone → Watch → Desktop → Smart Speaker → Car: same identity, same memory.
public struct CircleIdentity: Sendable {
    /// Stable GUID — never changes.
    public var identityId: String

    /// Human-readable display name.
    public var displayName: String

    /// Preferred BCP-47 language tag, e.g. "zu", "en". nil = unset.
    public var preferredLanguage: String?

    /// Trust tier for this identity.
    public var tier: IdentityTier

    /// All device IDs registered to this identity.
    public var deviceIds: [String]

    /// When this identity was first created (UTC).
    public var createdAt: Date

    /// When this identity was last seen active (UTC).
    public var lastSeenAt: Date

    public init(
        identityId: String,
        displayName: String,
        preferredLanguage: String? = nil,
        tier: IdentityTier,
        deviceIds: [String],
        createdAt: Date,
        lastSeenAt: Date
    ) {
        self.identityId = identityId
        self.displayName = displayName
        self.preferredLanguage = preferredLanguage
        self.tier = tier
        self.deviceIds = deviceIds
        self.createdAt = createdAt
        self.lastSeenAt = lastSeenAt
    }
}

// MARK: - RegisteredDevice

/// A device registered to an identity.
public struct RegisteredDevice: Sendable {
    /// Stable device identifier.
    public var deviceId: String

    /// The identity this device belongs to.
    public var identityId: String

    /// Platform string: "android" | "ios" | "windows" | "macos" | "linux" | "web" | "watch" | "iot"
    public var platform: String

    /// Human-readable device name. nil if unnamed.
    public var deviceName: String?

    /// When this device was first registered (UTC).
    public var registeredAt: Date

    /// When this device was last active (UTC).
    public var lastActiveAt: Date

    public init(
        deviceId: String,
        identityId: String,
        platform: String,
        deviceName: String? = nil,
        registeredAt: Date,
        lastActiveAt: Date
    ) {
        self.deviceId = deviceId
        self.identityId = identityId
        self.platform = platform
        self.deviceName = deviceName
        self.registeredAt = registeredAt
        self.lastActiveAt = lastActiveAt
    }
}

// MARK: - BiometricProfile

/// An enrolled biometric profile used for face-based identity matching.
/// The embeddingVector must be L2-normalised before storage.
public struct BiometricProfile: Sendable {
    /// The identity this profile belongs to.
    public let identityId: String

    /// L2-normalised face embedding vector.
    public let embeddingVector: [Float]

    /// Cosine-similarity threshold above which a candidate is considered a match.
    /// Default 0.85.
    public let matchThreshold: Float

    /// When this profile was enrolled (UTC).
    public let enrolledAt: Date

    /// When the profile was last successfully matched (UTC). nil if never matched.
    public let lastMatchAt: Date?

    /// Dimensionality of the embedding vector.
    public var embeddingDimension: Int { embeddingVector.count }

    public init(
        identityId: String,
        embeddingVector: [Float],
        matchThreshold: Float = 0.85,
        enrolledAt: Date,
        lastMatchAt: Date? = nil
    ) {
        self.identityId = identityId
        self.embeddingVector = embeddingVector
        self.matchThreshold = matchThreshold
        self.enrolledAt = enrolledAt
        self.lastMatchAt = lastMatchAt
    }
}

// MARK: - BiometricError

/// Errors raised by `BiometricMatcher`.
public enum BiometricError: Error, CustomStringConvertible, Sendable {
    /// The two embeddings have different dimensions, so no similarity between
    /// them means anything. Refused rather than scored: answering would be a
    /// false-match path, and 0.0 would read as "not this person" when the real
    /// answer is "these came from different models".
    case embeddingDimensionMismatch(a: Int, b: Int)

    public var description: String {
        switch self {
        case let .embeddingDimensionMismatch(a, b):
            return "Embedding dimension mismatch: a=\(a), b=\(b). "
                 + "Both vectors must come from the same model."
        }
    }
}

// MARK: - BiometricMatcher

/// Scalar cosine-similarity matcher for face embeddings.
/// IMPORTANT: Uses Double accumulators and a plain scalar loop.
/// Do NOT use Accelerate, vDSP, SIMD, or any hardware intrinsics.
/// The scalar-Double constraint ensures bit-identical results across
/// all platforms (arm64, x86_64, armv7) and runtimes (Swift, C#, Kotlin, Python…).
public enum BiometricMatcher {

    /// Computes the cosine similarity between two Float vectors.
    /// Returns a value in [-1.0, 1.0], or 0.0 for empty or zero-magnitude
    /// vectors. Vectors of DIFFERENT lengths are refused, not scored —
    /// see `BiometricError.embeddingDimensionMismatch`.
    ///
    /// Uses Double accumulators for cross-platform reproducibility.
    /// Do NOT use SIMD, vDSP, Accelerate framework, or any hardware intrinsics.
    public static func cosineSimilarity(_ a: [Float], _ b: [Float]) throws -> Double {
        guard a.count == b.count else {
            throw BiometricError.embeddingDimensionMismatch(a: a.count, b: b.count)
        }
        guard !a.isEmpty else { return 0.0 }
        var dot  = 0.0
        var magA = 0.0
        var magB = 0.0
        for i in 0..<a.count {
            let ai = Double(a[i])
            let bi = Double(b[i])
            dot  += ai * bi
            magA += ai * ai
            magB += bi * bi
        }
        magA = sqrt(magA)
        magB = sqrt(magB)
        guard magA > 1e-10, magB > 1e-10 else { return 0.0 }
        return max(-1.0, min(1.0, dot / (magA * magB)))
    }

    /// Returns true when the candidate embedding's cosine similarity to the
    /// enrolled profile meets or exceeds the profile's matchThreshold.
    ///
    /// Throws when the candidate and the enrolled profile have different
    /// dimensions — a mismatched model rather than a failed match, and worth
    /// surfacing instead of reporting as a quiet `false`.
    public static func isMatch(_ candidate: [Float], against profile: BiometricProfile) throws -> Bool {
        return try cosineSimilarity(candidate, profile.embeddingVector) >= Double(profile.matchThreshold)
    }
}

// MARK: - IBiometricStore

/// Persistent store for biometric profiles.
public protocol IBiometricStore {
    /// Returns the biometric profile for the given identityId, or nil if not enrolled.
    func get(identityId: String) async throws -> BiometricProfile?

    /// Stores or replaces the biometric profile.
    func save(_ profile: BiometricProfile) async throws

    /// Deletes the biometric profile. No-op if not found.
    func delete(identityId: String) async throws
}

// MARK: - IIdentityStore

/// Persistent store for Circle AI identities and device registrations.
public protocol IIdentityStore {
    /// Returns the identity with the given identityId, or nil if not found.
    func get(identityId: String) async throws -> CircleIdentity?

    /// Persists the identity.
    func save(_ identity: CircleIdentity) async throws

    /// Returns all devices registered to identityId.
    func getDevices(identityId: String) async throws -> [RegisteredDevice]

    /// Registers or updates a device.
    func registerDevice(_ device: RegisteredDevice) async throws

    /// Returns the identity that owns the given deviceId, or nil.
    func getByDevice(deviceId: String) async throws -> CircleIdentity?
}

// MARK: - IIdentityProvider

/// Resolves the active identity for the current device/session.
/// Implementations may use local storage, biometrics, or mesh-distributed keys.
public protocol IIdentityProvider {
    /// Returns the currently authenticated identity, or nil if unauthenticated.
    func getCurrentIdentity() async throws -> CircleIdentity?

    /// Returns true if an authenticated identity is active.
    func isAuthenticated() async throws -> Bool

    /// Creates a new identity with the given display name and optional language.
    func createIdentity(displayName: String, preferredLanguage: String?) async throws -> CircleIdentity
}
