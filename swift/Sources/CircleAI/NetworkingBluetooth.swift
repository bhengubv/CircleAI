// NetworkingBluetooth.swift
//
// Port of CircleAI.Networking.Bluetooth (the C# reference) — the BLE GATT
// network transport. Collapses the C# folder's two files
// (BluetoothTransportCommons.cs / BluetoothNetworkTransport.cs) into this single
// Swift file per the tree's flat convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.Bluetooth/):
//   Enum     — BluetoothConnectionState
//   DTOs     — BluetoothEndpointDescriptor, BluetoothCapabilityProfile,
//              BluetoothThroughputSample
//   Presets  — BluetoothCapabilityProfiles (Le5 / Le4 / Classic)
//   Helpers  — HttpStatusFamily analogue not present; N/A
//   Registry — InMemoryBluetoothTransportRegistry
//   Transport— BluetoothNetworkTransport (INetworkTransport) + IBleGattAdapter
//
// The C# transport wires an injected IBleGattAdapter (platform BLE) to an
// unbounded inbound Channel<NetworkPayload>: Start hands the adapter the channel
// WRITER so the adapter can push received frames; Send delegates to the adapter's
// WriteAsync; Receive drains the channel; Stop stops the adapter then completes
// the channel. This port preserves that seam exactly — the adapter is injected
// behind IBleGattAdapter and is handed an IBleInboundWriter (the Swift analogue
// of the ChannelWriter) on start. No sockets: a test adapter drives the loopback
// deterministically.
//
// Concurrency (same rules as Networking.swift):
//   • Snapshot continuations UNDER the NSLock and finish() OUTSIDE it — finish()
//     runs onTermination synchronously and re-enters the same non-reentrant lock.
//   • The inbound stream is single-consumer with UNBOUNDED buffering, so a frame
//     the adapter pushes before receive() is iterated is retained, not lost
//     (mirrors C#'s unbounded inbound Channel<NetworkPayload>).

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// BluetoothConnectionState (BluetoothTransportCommons.cs)
//
// Int-raw + Codable; ordinals follow the C# declaration order.
// ──────────────────────────────────────────────────────────────────────────

/// The connection lifecycle state of a Bluetooth endpoint. Ordinals mirror the
/// C# `BluetoothConnectionState` declaration order.
public enum BluetoothConnectionState: Int, Codable, Sendable, CaseIterable {
    case disconnected = 0
    case discovering = 1
    case connecting = 2
    case connected = 3
    case failed = 4
}

// ──────────────────────────────────────────────────────────────────────────
// BluetoothEndpointDescriptor / BluetoothCapabilityProfile /
// BluetoothThroughputSample (records)
// ──────────────────────────────────────────────────────────────────────────

/// Describes a discovered Bluetooth endpoint. Ported from the C#
/// `BluetoothEndpointDescriptor` record.
public struct BluetoothEndpointDescriptor: Sendable, Equatable, Codable {
    public let deviceId: String
    public let name: String
    public let macAddress: String
    public let advertisedServices: [String]

    public init(
        deviceId: String,
        name: String,
        macAddress: String,
        advertisedServices: [String]
    ) {
        self.deviceId = deviceId
        self.name = name
        self.macAddress = macAddress
        self.advertisedServices = advertisedServices
    }
}

/// Describes the capabilities of a Bluetooth radio/profile. Ported from the C#
/// `BluetoothCapabilityProfile` record.
public struct BluetoothCapabilityProfile: Sendable, Equatable, Codable {
    public let maxMtuBytes: Int
    public let supportsSecureConnections: Bool
    public let supportsHighSpeed: Bool
    public let compatibleProfiles: [String]

    public init(
        maxMtuBytes: Int,
        supportsSecureConnections: Bool,
        supportsHighSpeed: Bool,
        compatibleProfiles: [String]
    ) {
        self.maxMtuBytes = maxMtuBytes
        self.supportsSecureConnections = supportsSecureConnections
        self.supportsHighSpeed = supportsHighSpeed
        self.compatibleProfiles = compatibleProfiles
    }
}

/// A single throughput measurement to a device. Ported from the C#
/// `BluetoothThroughputSample` record.
public struct BluetoothThroughputSample: Sendable, Equatable, Codable {
    public let deviceId: String
    public let kbpsRead: Double
    public let kbpsWrite: Double
    public let atUtc: Date

    public init(deviceId: String, kbpsRead: Double, kbpsWrite: Double, atUtc: Date) {
        self.deviceId = deviceId
        self.kbpsRead = kbpsRead
        self.kbpsWrite = kbpsWrite
        self.atUtc = atUtc
    }
}

// ──────────────────────────────────────────────────────────────────────────
// BluetoothCapabilityProfiles (static presets)
// ──────────────────────────────────────────────────────────────────────────

/// Well-known Bluetooth capability presets. Ported from the C# static
/// `BluetoothCapabilityProfiles` (values match exactly).
public enum BluetoothCapabilityProfiles {
    /// Bluetooth LE 5.x: 247-byte MTU, secure + high-speed, GATT + L2CAP.
    public static let le5 = BluetoothCapabilityProfile(
        maxMtuBytes: 247,
        supportsSecureConnections: true,
        supportsHighSpeed: true,
        compatibleProfiles: ["GATT", "L2CAP"])

    /// Bluetooth LE 4.x: 23-byte MTU, secure, GATT only.
    public static let le4 = BluetoothCapabilityProfile(
        maxMtuBytes: 23,
        supportsSecureConnections: true,
        supportsHighSpeed: false,
        compatibleProfiles: ["GATT"])

    /// Bluetooth Classic: 1024-byte MTU, secure, SPP + RFCOMM.
    public static let classic = BluetoothCapabilityProfile(
        maxMtuBytes: 1024,
        supportsSecureConnections: true,
        supportsHighSpeed: false,
        compatibleProfiles: ["SPP", "RFCOMM"])
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryBluetoothTransportRegistry (BluetoothTransportCommons.cs)
//
// C# uses ConcurrentDictionary for endpoints/states and a lock-guarded List for
// throughput. Here a single NSLock guards all three; ordering/aggregation match.
// ──────────────────────────────────────────────────────────────────────────

/// In-memory registry of Bluetooth endpoints, connection states, and throughput
/// samples. Ported from the C# `InMemoryBluetoothTransportRegistry`.
public final class InMemoryBluetoothTransportRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var endpoints: [String: BluetoothEndpointDescriptor] = [:]
    private var states: [String: BluetoothConnectionState] = [:]
    private var throughput: [BluetoothThroughputSample] = []

    public init() {}

    /// Register (or replace) an endpoint keyed by `deviceId`.
    public func register(_ e: BluetoothEndpointDescriptor) {
        lock.lock(); endpoints[e.deviceId] = e; lock.unlock()
    }

    /// The endpoint for `deviceId`, or nil.
    public func getEndpoint(_ deviceId: String) -> BluetoothEndpointDescriptor? {
        lock.lock(); defer { lock.unlock() }
        return endpoints[deviceId]
    }

    /// All endpoints, ordered by `name` (matches C#'s `OrderBy(e => e.Name)`).
    public var allEndpoints: [BluetoothEndpointDescriptor] {
        lock.lock(); defer { lock.unlock() }
        return endpoints.values.sorted { $0.name < $1.name }
    }

    /// Set the connection state for a device.
    public func setState(_ deviceId: String, _ s: BluetoothConnectionState) {
        lock.lock(); states[deviceId] = s; lock.unlock()
    }

    /// The connection state for a device, or `.disconnected` (matches C#'s default).
    public func state(_ deviceId: String) -> BluetoothConnectionState {
        lock.lock(); defer { lock.unlock() }
        return states[deviceId] ?? .disconnected
    }

    /// Record a throughput sample.
    public func recordThroughput(_ s: BluetoothThroughputSample) {
        lock.lock(); throughput.append(s); lock.unlock()
    }

    /// Mean read throughput to `deviceId`. Empty → 0 (matches C#'s
    /// `DefaultIfEmpty(0.0).Average()`).
    public func avgKbpsRead(_ deviceId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        let rows = throughput.filter { $0.deviceId == deviceId }.map { $0.kbpsRead }
        guard !rows.isEmpty else { return 0 }
        return rows.reduce(0, +) / Double(rows.count)
    }

    /// Mean write throughput to `deviceId`. Empty → 0 (matches C#'s
    /// `AvgKbpsWrite` → `DefaultIfEmpty(0.0).Average()`).
    public func avgKbpsWrite(_ deviceId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        let rows = throughput.filter { $0.deviceId == deviceId }.map { $0.kbpsWrite }
        guard !rows.isEmpty else { return 0 }
        return rows.reduce(0, +) / Double(rows.count)
    }

    /// Drop a device from the registry: removes its endpoint descriptor and any
    /// tracked connection state. Returns true if an endpoint was actually
    /// removed (matches C#'s `Unregister`).
    @discardableResult
    public func unregister(_ deviceId: String) -> Bool {
        if deviceId.isEmpty { return false }
        lock.lock(); defer { lock.unlock() }
        let removed = endpoints.removeValue(forKey: deviceId) != nil
        states[deviceId] = nil
        return removed
    }

    /// Endpoints advertising a given GATT/SPP service, matched case-insensitively
    /// and ordered by device name — the discovery view a service scanner needs.
    /// Empty service yields nothing (matches C#'s `EndpointsWithService`).
    public func endpointsWithService(_ service: String) -> [BluetoothEndpointDescriptor] {
        if service.isEmpty { return [] }
        lock.lock(); defer { lock.unlock() }
        return endpoints.values
            .filter { $0.advertisedServices.contains { $0.caseInsensitiveCompare(service) == .orderedSame } }
            .sorted { $0.name < $1.name }
    }

    /// Number of devices currently in the `.connected` state (matches C#'s
    /// `ConnectedCount`).
    public var connectedCount: Int {
        lock.lock(); defer { lock.unlock() }
        return states.values.filter { $0 == .connected }.count
    }
}

// ──────────────────────────────────────────────────────────────────────────
// IBleInboundWriter / IBleGattAdapter (BluetoothNetworkTransport.cs)
//
// The C# adapter is handed a ChannelWriter<NetworkPayload> on StartAsync so it
// can push frames it reads off the radio. The Swift analogue of that writer is
// IBleInboundWriter: a narrow sink the adapter calls to deliver a received
// payload into the transport's inbound stream. Keeping it an injected interface
// (not a concrete stream) preserves the "adapter is the injected socket" seam.
// ──────────────────────────────────────────────────────────────────────────

/// The sink an `IBleGattAdapter` uses to push a received payload into the
/// transport's inbound stream. The Swift analogue of C#'s
/// `ChannelWriter<NetworkPayload>` handed to the adapter on start.
public protocol IBleInboundWriter: AnyObject, Sendable {
    /// Deliver a payload the adapter read off the radio into the inbound stream.
    /// Returns false once the inbound stream has been completed (stopped).
    @discardableResult
    func push(_ payload: NetworkPayload) -> Bool
}

/// Platform-specific BLE GATT operations. Implement per platform (MAUI, Windows,
/// Linux). Ported from the C# `IBleGattAdapter`. The `ChannelWriter` parameter
/// becomes an injected `IBleInboundWriter`.
public protocol IBleGattAdapter: AnyObject {
    /// True when the radio is currently usable.
    var isAvailable: Bool { get }

    /// Begin operation, retaining `inbound` to push received frames.
    func start(inbound: IBleInboundWriter) async throws

    /// Stop operation and release the radio.
    func stop() async throws

    /// Write a payload to the connected peer (the send path).
    func write(_ payload: NetworkPayload) async throws
}

// ──────────────────────────────────────────────────────────────────────────
// BluetoothNetworkTransport (BluetoothNetworkTransport.cs)
// ──────────────────────────────────────────────────────────────────────────

/// `INetworkTransport` over BLE GATT. Wires an injected `IBleGattAdapter` to an
/// unbounded inbound stream: `start` hands the adapter an `IBleInboundWriter`;
/// `send` delegates to the adapter's `write`; `receive` drains the inbound
/// stream; `stop` stops the adapter then completes the inbound stream. Mirrors
/// the C# `BluetoothNetworkTransport` exactly.
public final class BluetoothNetworkTransport: INetworkTransport, @unchecked Sendable {
    /// The inbound sink implementation handed to the adapter. Buffers frames
    /// pushed before `receive()` is iterated (unbounded) so none are lost.
    private final class InboundWriter: IBleInboundWriter, @unchecked Sendable {
        private let lock = NSLock()
        private var completed = false
        private var pending: [NetworkPayload] = []
        private var continuation: AsyncStream<NetworkPayload>.Continuation?

        @discardableResult
        func push(_ payload: NetworkPayload) -> Bool {
            lock.lock()
            if completed { lock.unlock(); return false }
            if let cont = continuation {
                cont.yield(payload)
            } else {
                pending.append(payload)
            }
            lock.unlock()
            return true
        }

        /// Attach a consumer, draining anything buffered before it attached.
        func stream() -> AsyncStream<NetworkPayload> {
            AsyncStream(bufferingPolicy: .unbounded) { continuation in
                lock.lock()
                if completed {
                    lock.unlock()
                    continuation.finish()
                    return
                }
                for p in pending { continuation.yield(p) }
                pending.removeAll()
                self.continuation = continuation
                lock.unlock()

                continuation.onTermination = { [weak self] _ in
                    guard let self else { return }
                    self.lock.lock(); self.continuation = nil; self.lock.unlock()
                }
            }
        }

        /// Complete the inbound stream (C#'s `_inbound.Writer.TryComplete()`).
        func complete() {
            // Snapshot, release, then finish() — onTermination re-enters the lock.
            lock.lock()
            completed = true
            let cont = continuation
            continuation = nil
            pending.removeAll()
            lock.unlock()
            cont?.finish()
        }
    }

    private let adapter: IBleGattAdapter
    private let inbound = InboundWriter()

    public init(adapter: IBleGattAdapter) {
        self.adapter = adapter
    }

    public var kind: TransportKind { .bluetooth }

    /// Mirrors C#'s `IsAvailable => _adapter.IsAvailable`.
    public var isAvailable: Bool { adapter.isAvailable }

    /// Hands the adapter the inbound writer (C#: `_adapter.StartAsync(_inbound.Writer, ct)`).
    public func start() async throws {
        try await adapter.start(inbound: inbound)
    }

    /// Stops the adapter, then completes the inbound stream (C#'s order).
    public func stop() async throws {
        try await adapter.stop()
        inbound.complete()
    }

    /// Delegates to the adapter's write (C#: `_adapter.WriteAsync(payload, ct)`).
    public func send(_ payload: NetworkPayload) async throws {
        try await adapter.write(payload)
    }

    /// Yields inbound payloads. Mirrors C#'s `_inbound.Reader.ReadAllAsync(ct)`.
    public func receive() -> AsyncStream<NetworkPayload> {
        inbound.stream()
    }
}
