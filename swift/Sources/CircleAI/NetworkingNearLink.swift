// NetworkingNearLink.swift
//
// Port of CircleAI.Networking.NearLink (the C# reference) — the Huawei SLE /
// NearLink network transport. Collapses the C# folder's two files
// (NearLinkTransport.cs / NearLinkTransportCommons.cs) into this single Swift
// file per the tree's flat convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.NearLink/):
//   Enums     — NearLinkPairingState, NearLinkPowerProfile
//   DTOs      — NearLinkDevice, NearLinkSession, NearLinkThroughputSample
//   Registry  — InMemoryNearLinkRegistry
//   Transport — NearLinkTransport (INetworkTransport) + INearLinkAdapter
//
// Injected-socket note — the C# NearLinkTransport wires an injected
// INearLinkAdapter (the Huawei DevEco NearLink SDK / HAL) to an unbounded inbound
// Channel<NetworkPayload>: StartAsync hands the adapter the channel WRITER so it
// can push frames it reads off the radio; SendAsync delegates to the adapter's
// SendAsync; ReceiveAsync drains the channel; StopAsync stops the adapter then
// completes the channel. This port preserves that seam exactly — the adapter is
// injected behind INearLinkAdapter and is handed an INearLinkInboundWriter (the
// Swift analogue of the ChannelWriter) on start. No sockets: a test adapter
// drives the loopback deterministically.
//
// Concurrency (same rules as Networking.swift):
//   • Snapshot continuations UNDER the NSLock and finish() OUTSIDE it — finish()
//     runs onTermination synchronously and re-enters the same non-reentrant lock.
//   • The inbound stream is single-consumer with UNBOUNDED buffering, so a frame
//     the adapter pushes before receive() is iterated is retained, not lost
//     (mirrors C#'s unbounded inbound Channel<NetworkPayload>).

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// NearLinkPairingState / NearLinkPowerProfile (NearLinkTransportCommons.cs)
//
// Int-raw + Codable; ordinals follow the C# declaration order.
// ──────────────────────────────────────────────────────────────────────────

/// The pairing lifecycle state of a NearLink device. Ordinals mirror the C#
/// `NearLinkPairingState` declaration order.
public enum NearLinkPairingState: Int, Codable, Sendable, CaseIterable {
    case unpaired = 0
    case pairing = 1
    case paired = 2
    case pairingFailed = 3
}

/// The radio power profile of a NearLink session. Ordinals mirror the C#
/// `NearLinkPowerProfile` declaration order.
public enum NearLinkPowerProfile: Int, Codable, Sendable, CaseIterable {
    case lowEnergy = 0
    case balanced = 1
    case highThroughput = 2
}

// ──────────────────────────────────────────────────────────────────────────
// NearLinkDevice / NearLinkSession / NearLinkThroughputSample (records)
// ──────────────────────────────────────────────────────────────────────────

/// Describes a NearLink device. Ported from the C# `NearLinkDevice` record.
public struct NearLinkDevice: Sendable, Equatable, Codable {
    public let deviceId: String
    public let friendlyName: String
    public let manufacturerId: String
    public let firmwareVersion: String

    public init(
        deviceId: String,
        friendlyName: String,
        manufacturerId: String,
        firmwareVersion: String
    ) {
        self.deviceId = deviceId
        self.friendlyName = friendlyName
        self.manufacturerId = manufacturerId
        self.firmwareVersion = firmwareVersion
    }
}

/// Describes an open NearLink session. Ported from the C# `NearLinkSession`
/// record. `startedUtc` is C#'s `DateTimeOffset`.
public struct NearLinkSession: Sendable, Equatable, Codable {
    public let sessionId: String
    public let deviceId: String
    public let powerProfile: NearLinkPowerProfile
    public let startedUtc: Date

    public init(
        sessionId: String,
        deviceId: String,
        powerProfile: NearLinkPowerProfile,
        startedUtc: Date
    ) {
        self.sessionId = sessionId
        self.deviceId = deviceId
        self.powerProfile = powerProfile
        self.startedUtc = startedUtc
    }
}

/// A single NearLink throughput measurement. Ported from the C#
/// `NearLinkThroughputSample` record.
public struct NearLinkThroughputSample: Sendable, Equatable, Codable {
    public let deviceId: String
    public let kbpsRead: Double
    public let kbpsWrite: Double
    public let rssiDbm: Int
    public let atUtc: Date

    public init(
        deviceId: String,
        kbpsRead: Double,
        kbpsWrite: Double,
        rssiDbm: Int,
        atUtc: Date
    ) {
        self.deviceId = deviceId
        self.kbpsRead = kbpsRead
        self.kbpsWrite = kbpsWrite
        self.rssiDbm = rssiDbm
        self.atUtc = atUtc
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryNearLinkRegistry (NearLinkTransportCommons.cs)
//
// C# uses ConcurrentDictionaries for devices/states/sessions and a lock-guarded
// List for throughput. Here a single NSLock guards all of it; ordering +
// aggregation match exactly, including AvgRssi's DefaultIfEmpty(-127).
// ──────────────────────────────────────────────────────────────────────────

/// In-memory registry of NearLink devices, pairing states, sessions, and
/// throughput samples. Ported from the C# `InMemoryNearLinkRegistry`.
public final class InMemoryNearLinkRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var devices: [String: NearLinkDevice] = [:]
    private var states: [String: NearLinkPairingState] = [:]
    private var sessions: [String: NearLinkSession] = [:]
    private var throughput: [NearLinkThroughputSample] = []

    public init() {}

    /// Register (or replace) a device keyed by `deviceId`.
    public func register(_ d: NearLinkDevice) {
        lock.lock(); devices[d.deviceId] = d; lock.unlock()
    }

    /// The device with `id`, or nil.
    public func getDevice(_ id: String) -> NearLinkDevice? {
        lock.lock(); defer { lock.unlock() }
        return devices[id]
    }

    /// All devices, ordered by `friendlyName` (matches C#'s
    /// `OrderBy(d => d.FriendlyName)`).
    public var allDevices: [NearLinkDevice] {
        lock.lock(); defer { lock.unlock() }
        return devices.values.sorted { $0.friendlyName < $1.friendlyName }
    }

    /// Set the pairing state for a device.
    public func setPairingState(_ deviceId: String, _ s: NearLinkPairingState) {
        lock.lock(); states[deviceId] = s; lock.unlock()
    }

    /// The pairing state for a device, or `.unpaired` (matches C#'s default).
    public func pairingState(_ deviceId: String) -> NearLinkPairingState {
        lock.lock(); defer { lock.unlock() }
        return states[deviceId] ?? .unpaired
    }

    /// Open (or replace) a session keyed by `sessionId`.
    public func openSession(_ s: NearLinkSession) {
        lock.lock(); sessions[s.sessionId] = s; lock.unlock()
    }

    /// The session with `id`, or nil.
    public func getSession(_ id: String) -> NearLinkSession? {
        lock.lock(); defer { lock.unlock() }
        return sessions[id]
    }

    /// Close (remove) the session with `id`.
    public func closeSession(_ id: String) {
        lock.lock(); sessions[id] = nil; lock.unlock()
    }

    /// Every active session (order unspecified, matching C#'s `Values.ToArray()`).
    public var activeSessions: [NearLinkSession] {
        lock.lock(); defer { lock.unlock() }
        return Array(sessions.values)
    }

    /// Record a throughput sample.
    public func recordThroughput(_ s: NearLinkThroughputSample) {
        lock.lock(); throughput.append(s); lock.unlock()
    }

    /// Mean RSSI (dBm) across samples for `deviceId`. Empty → -127 (matches C#'s
    /// `DefaultIfEmpty(-127).Average()`).
    public func avgRssi(_ deviceId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        let rows = throughput.filter { $0.deviceId == deviceId }.map { Double($0.rssiDbm) }
        guard !rows.isEmpty else { return -127 }
        return rows.reduce(0, +) / Double(rows.count)
    }
}

// ──────────────────────────────────────────────────────────────────────────
// INearLinkInboundWriter / INearLinkAdapter (NearLinkTransport.cs)
//
// The C# adapter is handed a ChannelWriter<NetworkPayload> on StartAsync so it
// can push frames it reads off the radio. The Swift analogue of that writer is
// INearLinkInboundWriter: a narrow sink the adapter calls to deliver a received
// payload into the transport's inbound stream. Keeping it an injected interface
// (not a concrete stream) preserves the "adapter is the injected socket" seam.
// ──────────────────────────────────────────────────────────────────────────

/// The sink an `INearLinkAdapter` uses to push a received payload into the
/// transport's inbound stream. The Swift analogue of C#'s
/// `ChannelWriter<NetworkPayload>` handed to the adapter on start.
public protocol INearLinkInboundWriter: AnyObject, Sendable {
    /// Deliver a payload the adapter read off the radio into the inbound stream.
    /// Returns false once the inbound stream has been completed (stopped).
    @discardableResult
    func push(_ payload: NetworkPayload) -> Bool
}

/// Platform-level NearLink / SLE operations. Implement using the Huawei DevEco
/// NearLink SDK on HarmonyOS, or the NearLink HAL on compatible Android devices.
/// Ported from the C# `INearLinkAdapter`. The `ChannelWriter` parameter becomes
/// an injected `INearLinkInboundWriter`.
public protocol INearLinkAdapter: AnyObject {
    /// True when the radio is currently usable.
    var isAvailable: Bool { get }

    /// Begin operation, retaining `inbound` to push received frames.
    func start(inbound: INearLinkInboundWriter) async throws

    /// Stop operation and release the radio.
    func stop() async throws

    /// Send a payload to the connected peer (the send path).
    func send(_ payload: NetworkPayload) async throws
}

// ──────────────────────────────────────────────────────────────────────────
// NearLinkTransport (NearLinkTransport.cs)
// ──────────────────────────────────────────────────────────────────────────

/// `INetworkTransport` for Huawei SLE / NearLink. Wires an injected
/// `INearLinkAdapter` to an unbounded inbound stream: `start` hands the adapter
/// an `INearLinkInboundWriter`; `send` delegates to the adapter's `send`;
/// `receive` drains the inbound stream; `stop` stops the adapter then completes
/// the inbound stream. Mirrors the C# `NearLinkTransport` exactly.
public final class NearLinkTransport: INetworkTransport, @unchecked Sendable {
    /// The inbound sink implementation handed to the adapter. Buffers frames
    /// pushed before `receive()` is iterated (unbounded) so none are lost.
    private final class InboundWriter: INearLinkInboundWriter, @unchecked Sendable {
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

    private let adapter: INearLinkAdapter
    private let inbound = InboundWriter()

    /// - Parameter adapter: the injected NearLink adapter (the socket seam).
    public init(adapter: INearLinkAdapter) {
        self.adapter = adapter
    }

    public var kind: TransportKind { .nearLink }

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

    /// Delegates to the adapter's send (C#: `_adapter.SendAsync(payload, ct)`).
    public func send(_ payload: NetworkPayload) async throws {
        try await adapter.send(payload)
    }

    /// Yields inbound payloads. Mirrors C#'s `_inbound.Reader.ReadAllAsync(ct)`.
    public func receive() -> AsyncStream<NetworkPayload> {
        inbound.stream()
    }
}
