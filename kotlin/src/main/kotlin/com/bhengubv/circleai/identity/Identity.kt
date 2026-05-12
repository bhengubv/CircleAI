// Identity.kt
//
// Kotlin port of Circle.AI.Identity portable layer.
//
// Covers:
//   IdentityTier       — enum: Anonymous | Pseudonymous | Verified
//   CircleIdentity     — unified persona key that travels with the person
//   RegisteredDevice   — a device registered to an identity
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
 * Implementations may use local storage, biometrics, or mesh-distributed keys.
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
