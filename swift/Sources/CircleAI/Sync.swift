// Sync.swift
//
// SyncDeliveryMode, SyncDomainKeys, SyncDelta, ISyncChannel.
// The cross-device continuity primitive that makes Circle AI HER + JARVIS:
// memory follows the person, not the device.

import Foundation

// MARK: - SyncDeliveryMode

/// Transport hint for how a SyncDelta should be delivered.
public enum SyncDeliveryMode: String, Sendable, CaseIterable {
    /// Best-effort real-time push (WebSocket / gRPC).
    case realtime
    /// Reliable in-order delivery (MQTT / retry queue).
    case reliable
    /// Delay-tolerant networking — delivery may be hours or days later.
    case dtn
    /// Store locally until a direct connection to the target is available.
    case localStore
}

// MARK: - SyncDomainKeys

/// Well-known domain key constants for standard Circle AI state domains.
public enum SyncDomainKeys {
    public static let memoryEpisodic = "memory.episodic"
    public static let affectState    = "affect.state"
    public static let persona        = "persona"
    public static let goals          = "goals"
    public static let identity       = "identity"
}

// MARK: - SyncDelta

/// An incremental state change that must reach every device owned by ownerId.
/// This is the primitive that makes Circle AI cross-device continuous.
public struct SyncDelta: Sendable {
    /// The identity whose state this delta belongs to.
    public var ownerId: String

    /// The device that produced this delta.
    public var sourceDeviceId: String

    /// The target device. Empty string means broadcast to all owned devices.
    public var targetDeviceId: String

    /// Domain key, e.g. "memory.episodic", "affect.state", "persona".
    public var domainKey: String

    /// Serialised payload bytes.
    public var payload: Data

    /// Monotonically increasing sequence number per owner + domain.
    public var sequence: Int64

    /// How this delta should be delivered.
    public var deliveryMode: SyncDeliveryMode

    /// Optional time-to-live in seconds. nil = no expiry.
    public var ttl: TimeInterval?

    /// When this delta was created (UTC).
    public var createdAt: Date

    public init(
        ownerId: String,
        sourceDeviceId: String,
        targetDeviceId: String = "",
        domainKey: String,
        payload: Data,
        sequence: Int64,
        deliveryMode: SyncDeliveryMode,
        ttl: TimeInterval? = nil,
        createdAt: Date = Date()
    ) {
        self.ownerId = ownerId
        self.sourceDeviceId = sourceDeviceId
        self.targetDeviceId = targetDeviceId
        self.domainKey = domainKey
        self.payload = payload
        self.sequence = sequence
        self.deliveryMode = deliveryMode
        self.ttl = ttl
        self.createdAt = createdAt
    }
}

// MARK: - ISyncChannel

/// The cross-device continuity primitive.
/// Pushes memory/state deltas across whatever transport is available:
/// gRPC over 5G, BLE mesh via a neighbour, DTN bundle arriving 6 hours later.
/// App code is identical in every case.
public protocol ISyncChannel: AnyObject {
    /// Push a delta. Channel selects transport and handles retries.
    /// Returns when accepted (not necessarily delivered for DTN/LocalStore).
    func pushDelta(_ delta: SyncDelta) async throws

    /// Asynchronously yields incoming deltas for the given owner.
    func receiveDeltas(ownerId: String) -> AsyncStream<SyncDelta>

    /// Returns the last acknowledged sequence number for owner + domain.
    func getLastSequence(ownerId: String, domainKey: String) async throws -> Int64
}
