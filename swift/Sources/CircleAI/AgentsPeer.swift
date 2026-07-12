// AgentsPeer.swift
//
// Port of CircleAI.Agents.Peer — agent-to-agent protocol over the Aether mesh.
//   • PeerAgent.cs                 — PeerAgent, AgentCapability
//   • IAgentPeerProtocol.cs        — IAgentPeerProtocol
//   • AgentInvocationException.cs  — AgentInvocationException
//   • AgentBus.cs                  — AgentBus (in-process channel-backed bus)
//   • InMemoryAgentPeerProtocol.cs — InMemoryAgentPeerProtocol (reference impl)
//
// `AgentMessage` / `AgentMessageKind` already live in AgentMessage.swift and
// are reused verbatim.
//
// Porting notes:
//   • `Guid` → `UUID`; `DateTimeOffset` → `Date`; `decimal` → `Decimal`;
//     `byte[]` → `Data`.
//   • The C# `ConcurrentDictionary` + `Channel<T>` transport is modelled with
//     `NSLock`-guarded dictionaries and an `AsyncStream<AgentMessage>` per
//     inbox. Each bus inbox owns an `AsyncStream.Continuation`; `Send` yields
//     onto it, `Unregister` finishes it so `receive` terminates cleanly.
//   • Invocation correlation: the C# packs the originating Invoke's 16 GUID
//     bytes as the payload prefix of every Response/Decline. The Swift port
//     keeps the identical convention using `AgentMessage.id`'s 16-byte layout
//     (see `uuidBytes` / `uuidFrom`), so a Response/Decline carries the Invoke
//     id in the first 16 payload bytes.
//   • `InvokeAsync`'s `TaskCompletionSource` + linked-CTS timeout is modelled
//     with a per-invocation `CheckedContinuation` stored under the lock and a
//     detached timeout `Task` that resumes it with a timeout error. First
//     resume wins (guarded by removal from the pending map under the lock).
//   • Signer / capability-handler are injected closures, exactly as C#.

import Foundation

// MARK: - Records

/// A capability advertised by a `PeerAgent`. (C# `AgentCapability`.)
public struct AgentCapability: Sendable, Equatable, Codable {
    /// Canonical capability name — e.g. "translate", "summarise", "navigate".
    public let name: String
    /// Semantic version of the capability contract.
    public let version: String
    /// Cost in `costCurrency`. `0` means free.
    public let costPerInvocation: Decimal
    /// Currency code. Defaults to "SDPKT" within the CircleAI ecosystem.
    public let costCurrency: String

    public init(name: String, version: String, costPerInvocation: Decimal, costCurrency: String) {
        self.name = name
        self.version = version
        self.costPerInvocation = costPerInvocation
        self.costCurrency = costCurrency
    }
}

/// A peer CircleAI agent discoverable over the Aether mesh. (C# `PeerAgent`.)
public struct PeerAgent: Sendable, Equatable, Codable {
    /// Local handle for this peer (stable per discovery session).
    public let id: UUID
    /// Hashed UHID identity reference — never raw PII. Routing key in `AgentMessage.toUhid`.
    public let uhidIdentityId: String
    /// User-chosen display label (e.g. "Sipho's Circle").
    public let displayName: String
    /// Capabilities this peer advertises.
    public let capabilities: [AgentCapability]
    /// DER-encoded P-256 public key from the peer's UhidKeyRing.
    public let publicKeyDer: Data
    /// Transport currently carrying this peer, or `nil` when offline.
    public let currentTransportId: String?
    /// UTC timestamp of the last message or heartbeat from this peer.
    public let lastSeenAt: Date

    public init(id: UUID, uhidIdentityId: String, displayName: String,
                capabilities: [AgentCapability], publicKeyDer: Data,
                currentTransportId: String?, lastSeenAt: Date) {
        self.id = id
        self.uhidIdentityId = uhidIdentityId
        self.displayName = displayName
        self.capabilities = capabilities
        self.publicKeyDer = publicKeyDer
        self.currentTransportId = currentTransportId
        self.lastSeenAt = lastSeenAt
    }

    /// Returns a copy with `lastSeenAt` replaced. (C# `peer with { LastSeenAt = … }`.)
    func withLastSeen(_ lastSeen: Date) -> PeerAgent {
        PeerAgent(id: id, uhidIdentityId: uhidIdentityId, displayName: displayName,
                  capabilities: capabilities, publicKeyDer: publicKeyDer,
                  currentTransportId: currentTransportId, lastSeenAt: lastSeen)
    }
}

// MARK: - Errors

/// Thrown when a peer declines an `invoke` or returns an error response.
/// (C# `AgentInvocationException`.)
public struct AgentInvocationError: Error, CustomStringConvertible {
    /// Human-readable failure message.
    public let message: String
    /// The peer that declined or errored, if known.
    public let peerUhid: String?
    /// The decline envelope returned by the peer, if any.
    public let declineMessage: AgentMessage?

    public init(_ message: String, peerUhid: String? = nil, declineMessage: AgentMessage? = nil) {
        self.message = message
        self.peerUhid = peerUhid
        self.declineMessage = declineMessage
    }

    public var description: String { message }
}

// MARK: - Protocol contract

/// Agent-to-agent protocol over the Aether mesh. (C# `IAgentPeerProtocol`.)
public protocol IAgentPeerProtocol: Sendable {
    /// Listens for `discover` broadcasts + already-registered peers for a short
    /// window, returning every peer observed.
    func discoverPeers() async -> [PeerAgent]

    /// Initiates a handshake with `targetUhid`. Returns the peer's identity on a
    /// successful greet, or `nil` if unreachable.
    func greet(_ targetUhid: String) async -> PeerAgent?

    /// Queries `targetUhid` for the capabilities it currently advertises.
    func queryCapabilities(_ targetUhid: String) async -> [AgentCapability]

    /// Invokes `capability` on `targetUhid` with `requestPayload`. Awaits a
    /// single `response` envelope, throwing `AgentInvocationError` on decline or
    /// timeout.
    func invoke(_ targetUhid: String, capability: AgentCapability,
                requestPayload: Data) async throws -> AgentMessage

    /// Streams every inbound message addressed to this agent (including "*"
    /// broadcasts). Terminates when the underlying transport is torn down.
    func streamInbox() -> AsyncStream<AgentMessage>
}

// MARK: - UUID <-> 16-byte helpers

/// Little helper bridging `UUID` and its 16-byte representation, mirroring the
/// C# `Guid.ToByteArray()` / `new Guid(span)` correlation convention.
enum AgentUuidBytes {
    static func toData(_ uuid: UUID) -> Data {
        let t = uuid.uuid
        return Data([t.0, t.1, t.2, t.3, t.4, t.5, t.6, t.7,
                     t.8, t.9, t.10, t.11, t.12, t.13, t.14, t.15])
    }

    static func from(_ data: Data) -> UUID? {
        guard data.count >= 16 else { return nil }
        let b = [UInt8](data.prefix(16))
        return UUID(uuid: (b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
                           b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]))
    }
}

// MARK: - AgentBus

/// In-process bus used to simulate a mesh of CircleAI peers for tests and
/// samples. Not a production transport. (C# `AgentBus`.)
///
/// Owns the peer registry and one `AsyncStream` inbox per registered peer.
/// `send` routes an envelope to the right inbox (or fans out on "*" broadcast).
public final class AgentBus: @unchecked Sendable {
    private let lock = NSLock()
    private var peers: [String: PeerAgent] = [:]
    private var continuations: [String: AsyncStream<AgentMessage>.Continuation] = [:]

    public init() {}

    /// Snapshot of every peer currently registered on the bus.
    public var registeredPeers: [PeerAgent] {
        lock.lock(); defer { lock.unlock() }
        return Array(peers.values)
    }

    /// Registers `peer` on the bus, creating its inbox if absent. Re-registering
    /// the same UHID replaces the prior record but keeps the live inbox.
    public func register(_ peer: PeerAgent) {
        lock.lock()
        peers[peer.uhidIdentityId] = peer
        if continuations[peer.uhidIdentityId] == nil {
            // Create-on-demand continuation stored so `send` can yield to it.
            _ = makeInboxLocked(peer.uhidIdentityId)
        }
        lock.unlock()
    }

    /// Removes `uhid` from the bus and finishes its inbox so any active
    /// `receive` enumerator terminates cleanly.
    public func unregister(_ uhid: String) {
        lock.lock()
        peers.removeValue(forKey: uhid)
        let cont = continuations.removeValue(forKey: uhid)
        lock.unlock()
        cont?.finish()
    }

    /// Tries to read the latest record for `uhid`.
    public func peer(_ uhid: String) -> PeerAgent? {
        lock.lock(); defer { lock.unlock() }
        return peers[uhid]
    }

    /// Routes `message` to its recipient(s). "*" fans out to every inbox except
    /// the sender's own. Unknown UHIDs are dropped silently (peer offline).
    public func send(_ message: AgentMessage) {
        lock.lock()
        if message.toUhid == "*" {
            let targets = continuations.filter { $0.key != message.fromUhid }.map { $0.value }
            lock.unlock()
            for c in targets { c.yield(message) }
            return
        }
        let cont = continuations[message.toUhid]
        lock.unlock()
        cont?.yield(message)
    }

    /// Streams every envelope delivered to `uhid`'s inbox. Terminates when the
    /// inbox is finished (via `unregister`).
    public func receive(_ uhid: String) -> AsyncStream<AgentMessage> {
        lock.lock(); defer { lock.unlock() }
        return makeInboxLocked(uhid)
    }

    /// Creates (or recreates) the inbox stream for `uhid`, storing its
    /// continuation. Must be called with `lock` held.
    private func makeInboxLocked(_ uhid: String) -> AsyncStream<AgentMessage> {
        var storedContinuation: AsyncStream<AgentMessage>.Continuation!
        let stream = AsyncStream<AgentMessage>(bufferingPolicy: .unbounded) { continuation in
            storedContinuation = continuation
        }
        // Finish any prior continuation before replacing it.
        continuations[uhid]?.finish()
        continuations[uhid] = storedContinuation
        return stream
    }
}

// MARK: - InMemoryAgentPeerProtocol

/// In-memory reference implementation of `IAgentPeerProtocol`, backed by an
/// `AgentBus`. Multiple instances sharing one bus simulate a mesh of CircleAI
/// peers. (C# `InMemoryAgentPeerProtocol`.)
public final class InMemoryAgentPeerProtocol: IAgentPeerProtocol, @unchecked Sendable {
    /// Discovery listen window (C# 50 ms).
    public static let defaultDiscoveryWindow: TimeInterval = 0.050
    /// Per-invocation timeout (C# 5 s).
    public static let defaultInvokeTimeout: TimeInterval = 5.0

    private let ownUhid: String
    private let bus: AgentBus
    private let ownCapabilities: [AgentCapability]
    private let ownPublicKey: Data
    private let signer: (@Sendable (Data) -> Data)?
    private let capabilityHandler: (@Sendable (AgentCapability, Data) -> Data?)?

    private let lock = NSLock()
    private var lastSeen: [String: Date] = [:]
    private var pendingInvocations: [UUID: CheckedContinuation<AgentMessage, Error>] = [:]
    private var disposed = false

    // External inbox surfaced to `streamInbox` consumers.
    private var externalContinuation: AsyncStream<AgentMessage>.Continuation?

    private var pumpTask: Task<Void, Never>?

    public var componentName: String { "InMemoryAgentPeerProtocol" }

    /// The UHID identity owned by this agent.
    public var ownUhidId: String { ownUhid }

    /// Creates a new instance, registers it on `bus`, and begins pumping the inbox.
    public init(ownUhid: String, bus: AgentBus, ownCapabilities: [AgentCapability],
                ownPublicKey: Data, signer: (@Sendable (Data) -> Data)? = nil,
                capabilityHandler: (@Sendable (AgentCapability, Data) -> Data?)? = nil) {
        precondition(!ownUhid.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "ownUhid required")
        self.ownUhid = ownUhid
        self.bus = bus
        self.ownCapabilities = ownCapabilities
        self.ownPublicKey = ownPublicKey
        self.signer = signer
        self.capabilityHandler = capabilityHandler

        bus.register(PeerAgent(
            id: UUID(), uhidIdentityId: ownUhid, displayName: ownUhid,
            capabilities: ownCapabilities, publicKeyDer: ownPublicKey,
            currentTransportId: "in-memory", lastSeenAt: Date()))

        // Start pumping the bus inbox on a detached task.
        let inbox = bus.receive(ownUhid)
        pumpTask = Task { [weak self] in
            for await message in inbox {
                guard let self = self else { return }
                self.recordLastSeen(message.fromUhid, at: message.sentAt)
                self.handleIncoming(message)
            }
        }
    }

    /// Tears down the protocol, unregisters from the bus, cancels the pump, and
    /// finishes the external inbox. (C# `Dispose`.)
    public func dispose() {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        let ext = externalContinuation
        externalContinuation = nil
        // Fail any still-pending invocations so awaiters don't hang forever.
        let pending = pendingInvocations
        pendingInvocations.removeAll()
        lock.unlock()

        for (_, cont) in pending {
            cont.resume(throwing: AgentInvocationError("Protocol disposed.", peerUhid: nil))
        }
        pumpTask?.cancel()
        bus.unregister(ownUhid)
        ext?.finish()
    }

    // MARK: Protocol methods

    public func discoverPeers() async -> [PeerAgent] {
        // Broadcast a Discover so peers can refresh their view of us.
        let announcement = AgentMessage.create(
            kind: .discover, fromUhid: ownUhid, toUhid: "*",
            contentType: "application/json", payload: Data(), signature: sign(Data()))
        bus.send(announcement)

        // Brief listen window so any registered peer's responses can land.
        try? await Task.sleep(nanoseconds: UInt64(Self.defaultDiscoveryWindow * 1_000_000_000))

        return bus.registeredPeers
            .filter { $0.uhidIdentityId != ownUhid }
            .map { withLastSeen($0) }
    }

    public func greet(_ targetUhid: String) async -> PeerAgent? {
        precondition(!targetUhid.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "targetUhid required")
        guard let peer = bus.peer(targetUhid) else { return nil }
        let greet = AgentMessage.create(
            kind: .greet, fromUhid: ownUhid, toUhid: targetUhid,
            contentType: "application/json", payload: Data(), signature: sign(Data()))
        bus.send(greet)
        return withLastSeen(peer)
    }

    public func queryCapabilities(_ targetUhid: String) async -> [AgentCapability] {
        precondition(!targetUhid.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "targetUhid required")
        guard let peer = bus.peer(targetUhid) else { return [] }
        return peer.capabilities
    }

    public func invoke(_ targetUhid: String, capability: AgentCapability,
                       requestPayload: Data) async throws -> AgentMessage {
        precondition(!targetUhid.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "targetUhid required")
        guard bus.peer(targetUhid) != nil else {
            throw AgentInvocationError(
                "Peer '\(targetUhid)' is not reachable on the current transport.", peerUhid: targetUhid)
        }

        let invokeMsg = AgentMessage.create(
            kind: .invoke, fromUhid: ownUhid, toUhid: targetUhid,
            contentType: "application/octet-stream", payload: requestPayload,
            signature: sign(requestPayload))
        let invokeId = invokeMsg.id

        // Register the pending continuation, then send, then arm a timeout.
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<AgentMessage, Error>) in
            lock.lock()
            if disposed {
                lock.unlock()
                continuation.resume(throwing: AgentInvocationError("Protocol disposed.", peerUhid: targetUhid))
                return
            }
            pendingInvocations[invokeId] = continuation
            lock.unlock()

            bus.send(invokeMsg)

            // Timeout task: first resume wins, guarded by removal under the lock.
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: UInt64(Self.defaultInvokeTimeout * 1_000_000_000))
                guard let self = self else { return }
                self.lock.lock()
                let cont = self.pendingInvocations.removeValue(forKey: invokeId)
                self.lock.unlock()
                cont?.resume(throwing: AgentInvocationError(
                    "Invocation of '\(capability.name)' on peer '\(targetUhid)' timed out.",
                    peerUhid: targetUhid))
            }
        }
    }

    public func streamInbox() -> AsyncStream<AgentMessage> {
        AsyncStream<AgentMessage>(bufferingPolicy: .unbounded) { continuation in
            lock.lock()
            if disposed {
                lock.unlock()
                continuation.finish()
                return
            }
            externalContinuation?.finish()
            externalContinuation = continuation
            lock.unlock()
        }
    }

    // MARK: Private

    private func handleIncoming(_ message: AgentMessage) {
        switch message.kind {
        case .response, .decline:
            completePending(message)
        case .invoke:
            routeInvoke(message)
        default:
            break
        }

        // Every inbound message is also surfaced to external consumers.
        lock.lock(); let ext = externalContinuation; lock.unlock()
        ext?.yield(message)
    }

    private func completePending(_ message: AgentMessage) {
        // Convention: Response/Decline carry the original Invoke's id in the
        // first 16 payload bytes.
        guard message.payload.count >= 16, let correlationId = AgentUuidBytes.from(message.payload) else {
            return
        }
        lock.lock()
        let cont = pendingInvocations.removeValue(forKey: correlationId)
        lock.unlock()

        guard let cont = cont else { return }
        if message.kind == .decline {
            cont.resume(throwing: AgentInvocationError(
                "Peer '\(message.fromUhid)' declined the invocation.",
                peerUhid: message.fromUhid, declineMessage: message))
        } else {
            cont.resume(returning: message)
        }
    }

    private func routeInvoke(_ invoke: AgentMessage) {
        guard let handler = capabilityHandler else { return }

        // The in-memory mock hands the first advertised capability to the handler.
        let capability = ownCapabilities.first
            ?? AgentCapability(name: "unknown", version: "0.0.0", costPerInvocation: 0, costCurrency: "SDPKT")

        let result = handler(capability, invoke.payload)
        let correlationPrefix = AgentUuidBytes.toData(invoke.id)

        if result == nil {
            let decline = AgentMessage.create(
                kind: .decline, fromUhid: ownUhid, toUhid: invoke.fromUhid,
                contentType: "application/octet-stream", payload: correlationPrefix,
                signature: sign(correlationPrefix))
            bus.send(decline)
            return
        }

        var responsePayload = correlationPrefix
        responsePayload.append(result!)
        let response = AgentMessage.create(
            kind: .response, fromUhid: ownUhid, toUhid: invoke.fromUhid,
            contentType: "application/octet-stream", payload: responsePayload,
            signature: sign(responsePayload))
        bus.send(response)
    }

    private func sign(_ data: Data) -> Data { signer?(data) ?? Data() }

    private func recordLastSeen(_ uhid: String, at date: Date) {
        lock.lock(); lastSeen[uhid] = date; lock.unlock()
    }

    private func withLastSeen(_ peer: PeerAgent) -> PeerAgent {
        lock.lock(); let ts = lastSeen[peer.uhidIdentityId]; lock.unlock()
        return peer.withLastSeen(ts ?? peer.lastSeenAt)
    }
}
