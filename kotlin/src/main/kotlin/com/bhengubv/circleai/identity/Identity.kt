// Identity.kt
//
// Kotlin port of Circle.AI.Identity portable layer.
//
// Covers:
//   IdentityTier       — enum: Anonymous | Pseudonymous | Verified
//   CircleIdentity     — unified persona key that travels with the person
//   RegisteredDevice   — a device registered to an identity
//   BiometricProfile   — L2-normalised face embedding enrolled for an identity
//   BiometricMatcher   — cosine similarity matcher (double accumulators, no SIMD)
//   IBiometricStore    — persistent store for biometric profiles
//   IIdentityStore     — persistent store for identities and device registrations
//   IIdentityProvider  — resolves the active identity for the current device/session

package com.bhengubv.circleai.identity

import java.time.Instant

// ---------------------------------------------------------------------------
// IdentityTier
// ---------------------------------------------------------------------------

/** The trust / verification level of a Circle AI identity. */
enum class IdentityTier {
    /** No verification; temporary or guest user. */
    Anonymous,
    /** Identity is claimed but not externally verified. */
    Pseudonymous,
    /** Identity has been externally verified (KYC or equivalent). */
    Verified,
}

// ---------------------------------------------------------------------------
// CircleIdentity
// ---------------------------------------------------------------------------

/**
 * A Circle AI identity — the unified persona key that travels with the person.
 * Phone → Watch → Desktop → Smart Speaker → Car: same identity, same memory.
 */
data class CircleIdentity(
    /** Stable GUID — never changes. */
    val identityId: String,
    val displayName: String,
    val preferredLanguage: String?,
    val tier: IdentityTier,
    val deviceIds: List<String>,
    val createdAt: Instant,
    val lastSeenAt: Instant
)

// ---------------------------------------------------------------------------
// RegisteredDevice
// ---------------------------------------------------------------------------

/**
 * A device registered to an identity.
 * [platform] is one of: "android" | "ios" | "windows" | "macos" | "linux" |
 * "web" | "watch" | "iot".
 */
data class RegisteredDevice(
    val deviceId: String,
    val identityId: String,
    /** "android" | "ios" | "windows" | "macos" | "linux" | "web" | "watch" | "iot" */
    val platform: String,
    val deviceName: String?,
    val registeredAt: Instant,
    val lastActiveAt: Instant
)

// ---------------------------------------------------------------------------
// BiometricProfile
// ---------------------------------------------------------------------------

/**
 * An enrolled biometric profile for an identity.
 * [embeddingVector] must be L2-normalised before storage.
 */
data class BiometricProfile(
    val identityId: String,
    /** L2-normalised embedding vector. Must be normalised before storage. */
    val embeddingVector: FloatArray,
    /** Cosine similarity threshold for accepting a match. */
    val matchThreshold: Float = 0.85f,
    val enrolledAt: Instant = Instant.now(),
    val lastMatchAt: Instant? = null
) {
    /** The dimensionality of the embedding. */
    val embeddingDimension: Int get() = embeddingVector.size

    // FloatArray is reference-equal by default; override for value semantics.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is BiometricProfile) return false
        return identityId == other.identityId &&
            embeddingVector.contentEquals(other.embeddingVector) &&
            matchThreshold == other.matchThreshold &&
            enrolledAt == other.enrolledAt &&
            lastMatchAt == other.lastMatchAt
    }

    override fun hashCode(): Int {
        var result = identityId.hashCode()
        result = 31 * result + embeddingVector.contentHashCode()
        result = 31 * result + matchThreshold.hashCode()
        result = 31 * result + enrolledAt.hashCode()
        result = 31 * result + (lastMatchAt?.hashCode() ?: 0)
        return result
    }
}

// ---------------------------------------------------------------------------
// BiometricMatcher
// ---------------------------------------------------------------------------

/**
 * Computes cosine similarity between two float embedding vectors.
 *
 * CRITICAL CONSTRAINTS:
 * - Uses Double accumulators for cross-platform reproducibility.
 * - Do NOT use SIMD or hardware intrinsics.
 * - Results must match all other language implementations within 1e-5.
 */
object BiometricMatcher {

    /**
     * Computes the cosine similarity between vectors [a] and [b].
     * Both vectors must have the same non-zero length.
     *
     * Returns a value in [-1.0, 1.0].
     * Returns 0.0 if either vector has near-zero magnitude (< 1e-10).
     *
     * Uses Double accumulators for cross-platform reproducibility.
     * Do NOT use SIMD or hardware intrinsics.
     */
    fun cosineSimilarity(a: FloatArray, b: FloatArray): Double {
        require(a.size == b.size) { "Vectors must have equal length: ${a.size} != ${b.size}" }
        require(a.isNotEmpty()) { "Vectors must not be empty" }

        var dot  = 0.0
        var magA = 0.0
        var magB = 0.0

        for (i in a.indices) {
            val ai = a[i].toDouble()
            val bi = b[i].toDouble()
            dot  += ai * bi
            magA += ai * ai
            magB += bi * bi
        }

        magA = Math.sqrt(magA)
        magB = Math.sqrt(magB)

        if (magA < 1e-10 || magB < 1e-10) return 0.0

        return (dot / (magA * magB)).coerceIn(-1.0, 1.0)
    }

    /**
     * Returns true if [candidate] matches the [stored] biometric profile.
     * Match is accepted when cosine similarity >= [BiometricProfile.matchThreshold].
     */
    fun isMatch(candidate: FloatArray, stored: BiometricProfile): Boolean =
        cosineSimilarity(candidate, stored.embeddingVector) >= stored.matchThreshold.toDouble()
}

// ---------------------------------------------------------------------------
// IBiometricStore
// ---------------------------------------------------------------------------

/** Persistent store for biometric profiles. */
interface IBiometricStore {
    /** Returns the biometric profile for [identityId], or null if not enrolled. */
    suspend fun getAsync(identityId: String): BiometricProfile?

    /** Enrolls or replaces the biometric profile for an identity. */
    suspend fun saveAsync(profile: BiometricProfile)

    /** Removes the biometric profile for [identityId]. No-op if not found. */
    suspend fun deleteAsync(identityId: String)
}

// ---------------------------------------------------------------------------
// IIdentityStore
// ---------------------------------------------------------------------------

/** Persistent store for Circle AI identities and device registrations. */
interface IIdentityStore {
    /** Returns the identity with [identityId], or null if not found. */
    suspend fun getAsync(identityId: String): CircleIdentity?

    /** Persists the given identity (insert or replace). */
    suspend fun saveAsync(identity: CircleIdentity)

    /** Returns all devices registered to the given identity. */
    suspend fun getDevicesAsync(identityId: String): List<RegisteredDevice>

    /** Registers (or updates) a device record. */
    suspend fun registerDeviceAsync(device: RegisteredDevice)

    /** Looks up the identity that owns [deviceId], or null if not found. */
    suspend fun getByDeviceAsync(deviceId: String): CircleIdentity?
}

// ---------------------------------------------------------------------------
// IIdentityProvider
// ---------------------------------------------------------------------------

/**
 * Resolves the active identity for the current device/session.
 */
interface IIdentityProvider {
    /** Returns the currently authenticated identity, or null if unauthenticated. */
    suspend fun getCurrentIdentityAsync(): CircleIdentity?

    /** Returns true if there is a valid authenticated identity for this device/session. */
    suspend fun isAuthenticatedAsync(): Boolean

    /**
     * Creates a new identity with the given [displayName] and optional
     * [preferredLanguage] (IETF BCP-47). Returns the newly created identity.
     */
    suspend fun createIdentityAsync(
        displayName: String,
        preferredLanguage: String? = null
    ): CircleIdentity
}
