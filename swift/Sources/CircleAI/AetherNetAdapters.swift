// AetherNetAdapters.swift
//
// The join between CircleAI's Aether seam and a live AetherNet runtime.
//
// THE PAIR THAT MATTERS IS THE SYMMETRY. Directives flow both ways and each
// direction has its own adapter, which is the only way the round trip stays
// honest:
//
//   AetherNetDirectiveSink            CircleAI  →  AetherNet   (outbound)
//   AetherNetInboundDirectiveBridge   AetherNet →  CircleAI    (inbound)
//
// When the assistant decides a node is hostile, that decision crosses the sink
// and lands on the mesh's policy engine, which decides whether to HONOUR it —
// a directive is a recommendation to a peer, not a command over it. When a peer
// or another local consumer publishes one, the bridge carries it back the other
// way. Without both, security decisions travel in one direction and a device
// can be told nothing by the network it is part of.
//
// AGAINST A SEAM, NOT A DEPENDENCY. The C# side references the AetherNet
// assembly directly. The Swift package has no dependencies, and the aether
// protocol is its own repository, so the far side is a protocol here: the
// TRANSLATION crosses, and a host binds it to the real runtime.
//
// Ported from src/CircleAI.AetherNet/{AetherNetContextAdapter,
// AetherNetDirectiveSink, AetherNetInboundDirectiveBridge,
// AetherNetTelemetryAdapter, AetherNetCompanionStateChannel,
// CircleAiAetherNetAiProvider}.cs.

import Foundation

// MARK: - The far side

/// The mesh runtime's own directive consumer. Its own type, not CircleAI's:
/// the two shapes are separate on purpose, and collapsing them would make the
/// translation invisible and then wrong.
public protocol IMeshDirectiveConsumer: AnyObject, Sendable {
    func onMeshDirective(_ directive: SecurityDirective)
}

/// The mesh runtime's telemetry publisher.
public protocol IMeshTelemetryPublisher: AnyObject {
    func subscribe(_ observer: IAetherTelemetryObserver) -> IAetherSubscription
}

// MARK: - Context

/// Reports what the live AetherNet runtime is, to a caller that only knows the
/// Aether seam.
///
/// The install level is fixed at `.app`, deliberately: AetherNet runs as an
/// in-process library here, and an OS-managed instance is a different adapter
/// on a different platform. Reporting `.os` would make `requiresAuth` true and
/// send a caller looking for a permission prompt that will never appear.
public final class AetherNetContextAdapter: IAetherContext, @unchecked Sendable {

    public let runtimeVersion: SemanticVersion?
    public let minimumRequired: SemanticVersion?
    public let isEnabled: Bool

    public init(protocolVersion: Int,
                minimumRequired: SemanticVersion? = nil,
                isEnabled: Bool = true) {
        // The mesh protocol version IS the major version. A mesh speaking
        // protocol 4 and one speaking 5 are not the same runtime, and a caller
        // comparing versions has to see that in the number it is handed.
        self.runtimeVersion = SemanticVersion(major: protocolVersion)
        self.minimumRequired = minimumRequired
        self.isEnabled = isEnabled
    }

    public var installLevel: AetherInstallLevel { .app }

    /// True: this adapter only exists when the runtime is linked in.
    public var isAvailable: Bool { true }

    public var isSufficient: Bool {
        guard let minimumRequired else { return true }
        guard let runtimeVersion else { return false }
        return runtimeVersion >= minimumRequired
    }

    public var requiresAuth: Bool { installLevel == .os }
}

// MARK: - Directives, outbound

/// CircleAI → AetherNet.
///
/// The mesh's policy engine decides whether to honour what arrives. That is the
/// whole shape of the relationship: this device can say "I think that node is
/// hostile", and the network decides what to do about it.
public final class AetherNetDirectiveSink: ISecurityDirectiveConsumer, @unchecked Sendable {

    private let mesh: any IMeshDirectiveConsumer

    public init(mesh: any IMeshDirectiveConsumer) {
        self.mesh = mesh
    }

    public func onDirective(_ directive: SecurityDirective) {
        mesh.onMeshDirective(directive)
    }
}

// MARK: - Directives, inbound

/// AetherNet → CircleAI.
///
/// The inverse of the sink, and it is not optional. Without it a device issues
/// security decisions and receives none, so a node the rest of the mesh has
/// already agreed is hostile stays trusted here.
public final class AetherNetInboundDirectiveBridge: IMeshDirectiveConsumer, @unchecked Sendable {

    private let circle: any ISecurityDirectiveConsumer

    public init(circle: any ISecurityDirectiveConsumer) {
        self.circle = circle
    }

    public func onMeshDirective(_ directive: SecurityDirective) {
        circle.onDirective(directive)
    }
}

// MARK: - Telemetry

/// Fans AetherNet's telemetry out to a CircleAI observer.
///
/// The returned handle unhooks JUST that subscriber. A shared handle is how one
/// component shutting down takes the security layer's feed with it, and the
/// symptom is a mesh that silently stops being watched.
public final class AetherNetTelemetryAdapter: IAetherTelemetry, @unchecked Sendable {

    private let mesh: any IMeshTelemetryPublisher

    public init(mesh: any IMeshTelemetryPublisher) {
        self.mesh = mesh
    }

    public func subscribe(_ observer: IAetherTelemetryObserver) -> IAetherSubscription {
        mesh.subscribe(observer)
    }
}

// MARK: - Companion state over the mesh

/// What a companion tells its other devices about itself.
public struct CompanionStateMessage: Sendable, Equatable, Codable {
    public let deviceId: String
    public let payloadJson: String
    public let at: Date

    public init(deviceId: String, payloadJson: String, at: Date) {
        self.deviceId = deviceId
        self.payloadJson = payloadJson
        self.at = at
    }
}

/// Carries companion state between a person's own devices over the mesh.
///
/// A DEVICE NEVER RECEIVES ITS OWN BROADCAST. Without that check a two-device
/// pairing echoes state back and forth forever, and each device treats its own
/// message as news from the other one.
public final class AetherNetCompanionStateChannel: @unchecked Sendable {

    private let deviceId: String
    private let send: @Sendable (CompanionStateMessage) -> Void

    private let lock = NSLock()
    private var observers: [Int: @Sendable (CompanionStateMessage) -> Void] = [:]
    private var nextId = 0
    private var seen: Set<String> = []

    public init(deviceId: String,
                send: @escaping @Sendable (CompanionStateMessage) -> Void) {
        self.deviceId = deviceId
        self.send = send
    }

    public func publish(payloadJson: String, at: Date = Date()) {
        send(CompanionStateMessage(deviceId: deviceId, payloadJson: payloadJson, at: at))
    }

    /// Called by the host when the mesh delivers a message.
    ///
    /// Returns whether the message was ACCEPTED - new, and not our own echo.
    /// Deliberately not "did an observer hear it": whether anything is
    /// currently observing is the host's business and changes minute to minute,
    /// and folding it in here would make an echo, a duplicate and an unobserved
    /// message all report false, which is the distinction this value exists to
    /// draw.
    @discardableResult
    public func receive(_ message: CompanionStateMessage) -> Bool {
        if message.deviceId == deviceId { return false }        // our own echo

        // A mesh floods, so the SAME message legitimately arrives by more than
        // one route. Delivering it twice makes a companion apply the same state
        // change twice, which for anything non-idempotent is a real bug.
        let key = "\(message.deviceId)|\(message.at.timeIntervalSince1970)|\(message.payloadJson)"
        lock.lock()
        if seen.contains(key) { lock.unlock(); return false }
        seen.insert(key)
        let targets = Array(observers.values)
        lock.unlock()

        for o in targets { o(message) }
        return true
    }

    @discardableResult
    public func observe(_ handler: @escaping @Sendable (CompanionStateMessage) -> Void) -> Int {
        lock.lock(); defer { lock.unlock() }
        nextId += 1
        observers[nextId] = handler
        return nextId
    }

    public func stopObserving(_ token: Int) {
        lock.lock(); observers.removeValue(forKey: token); lock.unlock()
    }

    /// Clears the duplicate-suppression set. Called when a session restarts, so
    /// a long-lived device does not grow the set without bound.
    public func forgetSeen() {
        lock.lock(); seen.removeAll(); lock.unlock()
    }

    public var seenCount: Int {
        lock.lock(); defer { lock.unlock() }
        return seen.count
    }
}

// MARK: - AI over the mesh

/// Answers a prompt by asking a peer that has a model this device does not.
///
/// The point of the whole arrangement: a cheap phone with no room for a
/// generalist can still get an answer from one on the same mesh, without either
/// device reaching the internet.
public final class CircleAiAetherNetAiProvider: @unchecked Sendable {

    public typealias Ask = @Sendable (_ prompt: String, _ peerId: String) async throws -> String

    private let peers: @Sendable () -> [String]
    private let ask: Ask

    public init(peers: @escaping @Sendable () -> [String], ask: @escaping Ask) {
        self.peers = peers
        self.ask = ask
    }

    public var hasPeer: Bool { !peers().isEmpty }

    /// Asks each capable peer in turn until one answers.
    ///
    /// IN TURN, not in parallel: every attempt costs the peer's battery and the
    /// radio's airtime, and asking four phones a question one of them will
    /// answer wastes three of them. The mesh is shared, and a device that
    /// broadcasts every question to everybody is the reason mesh networks get
    /// switched off.
    public func complete(prompt: String) async throws -> String {
        let candidates = peers()
        guard !candidates.isEmpty else { throw AetherNetProviderError.noPeer }

        var lastError: Error?
        for peer in candidates {
            do {
                let answer = try await ask(prompt, peer)
                if !answer.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    return answer
                }
            } catch {
                // A peer that went out of range mid-question is ordinary on a
                // mesh, not an error worth ending on.
                lastError = error
            }
        }
        throw AetherNetProviderError.everyPeerFailed(lastError)
    }
}

public enum AetherNetProviderError: Error, CustomStringConvertible {
    case noPeer
    case everyPeerFailed(Error?)

    public var description: String {
        switch self {
        case .noPeer:
            return "No peer on the mesh is offering a model."
        case .everyPeerFailed(let e):
            return "Every peer that was asked failed"
                + (e.map { ": \($0)" } ?? " or answered with nothing.")
        }
    }
}
