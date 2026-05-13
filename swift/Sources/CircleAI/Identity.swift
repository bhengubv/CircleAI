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
