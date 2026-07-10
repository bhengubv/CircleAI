// SecurityWatchdog.swift
//
// Port of the local-runtime "immune system" surface from src/CircleAI.Security:
//   • SecurityCheckpoint.cs             — self-verifying state snapshot
//   • SecurityResponse.cs               — SecurityResponseKind + SecurityResponse
//   • UhidKeyRing.cs                    — ephemeral P-256 ECDSA session key ring
//   • RedactedEvidenceJsonConverter.cs  — evidence hashing on serialisation
//   • ISecurityWatchdog.cs              — ISecurityWatchdog + DefaultSecurityWatchdog
//   • IAnomalyEventDispatcher.cs        — dispatcher + Default + outcome/result
//
// ThreatVector + AnomalySignal already live in Security.swift; this file extends
// AnomalySignal with the redacting `Encodable` conformance that the C# reference
// gets from the property-level [JsonConverter(typeof(RedactedEvidenceJsonConverter))].
//
// Crypto: CryptoKit (P-256 ECDSA, SHA-256) — the same primitive HerJarvis.swift
// and ModelRuntime.swift use. No external NuGet/SPM dependency.
//
// Concurrency: DefaultSecurityWatchdog exposes a live signal stream. Subscribers
// are registered synchronously; the buffered broadcast retains signals emitted
// before a subscriber attaches (matching the C# unbounded Channel<AnomalySignal>).
// Continuations are finished OUTSIDE the lock (snapshot-then-release) so
// AsyncStream.finish() → onTermination cannot self-deadlock the NSLock.

import Foundation
import CryptoKit

// MARK: - SecurityCheckpoint

/// An immutable, self-verifying snapshot of trusted local state.
/// Created before a risky operation; used for rollback if an `AnomalySignal` is
/// confirmed.
///
/// - IMMUTABLE once created.
/// - SELF-VERIFYING (SHA-256 of `payload`, verified on restore).
/// - TAGGED with the UHID that created it (identity binding).
///
/// The payload is deliberately opaque (`Data`) so any module can checkpoint its
/// own serialised state without this type taking a dependency on it.
public struct SecurityCheckpoint: Sendable, Equatable {
    /// Unique checkpoint identifier.
    public let id: UUID
    /// The UHID of the local user whose state is captured. Binds the checkpoint
    /// to a specific identity.
    public let uhidIdentityId: String
    /// Label for the module or subsystem that created this checkpoint
    /// (e.g. "CircleAI.Companion", "CircleAI.Memory").
    public let moduleLabel: String
    /// Opaque serialised state payload.
    public let payload: Data
    /// SHA-256 hash of `payload`, computed at creation time. Verified by
    /// `verify()` before restoring.
    public let payloadHash: Data
    /// UTC timestamp of checkpoint creation.
    public let createdAt: Date

    public init(
        id: UUID,
        uhidIdentityId: String,
        moduleLabel: String,
        payload: Data,
        payloadHash: Data,
        createdAt: Date
    ) {
        self.id = id
        self.uhidIdentityId = uhidIdentityId
        self.moduleLabel = moduleLabel
        self.payload = payload
        self.payloadHash = payloadHash
        self.createdAt = createdAt
    }

    /// Creates a new checkpoint, computing `payloadHash` automatically.
    /// Mirrors the C# `Create` guards (non-blank uhid + module).
    public static func create(
        uhidIdentityId: String,
        moduleLabel: String,
        payload: Data
    ) -> SecurityCheckpoint {
        precondition(!uhidIdentityId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "uhidIdentityId required")
        precondition(!moduleLabel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "moduleLabel required")
        let hash = Data(SHA256.hash(data: payload))
        return SecurityCheckpoint(
            id: UUID(),
            uhidIdentityId: uhidIdentityId,
            moduleLabel: moduleLabel,
            payload: payload,
            payloadHash: hash,
            createdAt: Date())
    }

    /// Verifies that `payload` has not been tampered with since the checkpoint
    /// was created. Returns `true` if the current SHA-256 of `payload` matches
    /// `payloadHash`; `false` if the payload was modified.
    ///
    /// Uses a constant-time comparison (mirrors C#'s
    /// `CryptographicOperations.FixedTimeEquals`).
    public func verify() -> Bool {
        let current = Data(SHA256.hash(data: payload))
        return Self.fixedTimeEquals(current, payloadHash)
    }

    /// A non-sensitive textual representation — the payload bytes are NEVER
    /// included in clear. Only the first 16 hex chars (8 bytes) of `payloadHash`
    /// are emitted, sufficient for correlation across logs without leaking
    /// content.
    public var debugDescription: String {
        let hashPrefix: String = payloadHash.count >= 8
            ? Self.hexUpper(payloadHash.prefix(8))
            : "(empty)"
        return "SecurityCheckpoint(Id=\(id.uuidString.lowercased()), Module=\(moduleLabel), " +
               "Uhid=\(uhidIdentityId), PayloadSha256=\(hashPrefix)…, " +
               "PayloadBytes=\(payload.count), CreatedAt=\(NetJson.iso8601Round(createdAt)))"
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// Constant-time byte comparison. Returns false immediately on length
    /// mismatch (as .NET's `FixedTimeEquals` does), otherwise compares every
    /// byte without early exit.
    static func fixedTimeEquals(_ a: Data, _ b: Data) -> Bool {
        if a.count != b.count { return false }
        var diff: UInt8 = 0
        let ab = [UInt8](a)
        let bb = [UInt8](b)
        for i in 0..<ab.count { diff |= ab[i] ^ bb[i] }
        return diff == 0
    }

    static func hexUpper<S: Sequence>(_ bytes: S) -> String where S.Element == UInt8 {
        bytes.map { String(format: "%02X", $0) }.joined()
    }
}

// MARK: - SecurityResponseKind

/// The type of protective action taken in response to an `AnomalySignal`.
///
/// Ordinals follow the C# declaration order.
public enum SecurityResponseKind: Int, Codable, Sendable, CaseIterable {
    /// No action — confidence below threshold or vector is informational.
    case noAction = 0
    /// The session's ephemeral UHID key ring was regenerated; prior session keys
    /// are revoked and all in-flight requests using old keys will fail.
    case keyRotation = 1
    /// The affected session or execution sandbox was marked untrusted and
    /// isolated from the rest of the runtime.
    case sessionRevocation = 2
    /// A `PeerDirective` was issued to surrounding mesh nodes to isolate the
    /// suspected attack origin.
    case meshIsolationSignal = 3
    /// State was rolled back to the most recent verified `SecurityCheckpoint`.
    case stateRollback = 4
    /// A combination of responses was applied (e.g. key rotation + mesh
    /// isolation). See `SecurityResponse.appliedActions` for the full list.
    case composite = 5
}

// MARK: - SecurityResponse

/// Describes the protective action taken by `ISecurityWatchdog` in response to
/// an `AnomalySignal`. Returned from `onAnomalyDetected` so calling code knows
/// what was done.
public struct SecurityResponse: Sendable, Equatable {
    /// Identifier of the `AnomalySignal` that triggered this response.
    public let signalId: UUID
    /// Primary response kind.
    public let kind: SecurityResponseKind
    /// When `kind` is `.composite`, lists each individual action applied. Empty
    /// for single-action responses.
    public let appliedActions: [SecurityResponseKind]
    /// Human-readable description of what was done and why.
    public let description: String
    /// The `SecurityCheckpoint` that was restored, if any. `nil` when `kind` is
    /// not `.stateRollback` (or a composite that included a rollback).
    public let restoredCheckpoint: SecurityCheckpoint?
    /// UTC timestamp of the response.
    public let respondedAt: Date

    public init(
        signalId: UUID,
        kind: SecurityResponseKind,
        appliedActions: [SecurityResponseKind],
        description: String,
        restoredCheckpoint: SecurityCheckpoint?,
        respondedAt: Date
    ) {
        self.signalId = signalId
        self.kind = kind
        self.appliedActions = appliedActions
        self.description = description
        self.restoredCheckpoint = restoredCheckpoint
        self.respondedAt = respondedAt
    }

    /// Creates a no-action response for low-confidence or informational signals.
    public static func noAction(signalId: UUID, reason: String) -> SecurityResponse {
        SecurityResponse(signalId: signalId, kind: .noAction,
                         appliedActions: [], description: reason,
                         restoredCheckpoint: nil, respondedAt: Date())
    }

    /// Creates a key-rotation response.
    public static func forKeyRotation(signalId: UUID, description: String) -> SecurityResponse {
        SecurityResponse(signalId: signalId, kind: .keyRotation,
                         appliedActions: [], description: description,
                         restoredCheckpoint: nil, respondedAt: Date())
    }

    /// Creates a state-rollback response, recording the restored checkpoint.
    public static func forRollback(signalId: UUID, restored: SecurityCheckpoint) -> SecurityResponse {
        SecurityResponse(signalId: signalId, kind: .stateRollback,
                         appliedActions: [],
                         description: "State rolled back to checkpoint \(restored.id.uuidString.lowercased()) (\(restored.moduleLabel)).",
                         restoredCheckpoint: restored, respondedAt: Date())
    }

    /// Creates a composite response from multiple individual actions.
    public static func composite(
        signalId: UUID,
        actions: [SecurityResponseKind],
        description: String,
        restoredCheckpoint: SecurityCheckpoint? = nil
    ) -> SecurityResponse {
        SecurityResponse(signalId: signalId, kind: .composite,
                         appliedActions: actions, description: description,
                         restoredCheckpoint: restoredCheckpoint, respondedAt: Date())
    }
}

// MARK: - UhidKeyRing

/// Error thrown when signing with a revoked or disposed key ring.
public enum UhidKeyRingError: Error, Equatable {
    /// The ring was explicitly revoked — call `rotate()` to get a fresh ring.
    case revoked(ringId: UUID)
    /// The ring was disposed and no longer holds a key.
    case disposed
}

/// Ephemeral ECDSA (P-256) session key ring bound to a UHID identity.
/// Generate a fresh ring at session start or on anomaly confirmation. Once
/// revoked, the ring cannot sign; generate a new one.
///
/// P-256 is selected over Ed25519 for cross-language BCL/toolchain compatibility
/// (the C# reference uses `ECDsa` on `nistP256`; here we use CryptoKit's
/// `P256.Signing`). `Verify` continues to work after revocation so prior
/// signatures remain checkable.
public final class UhidKeyRing: @unchecked Sendable {
    private let lock = NSLock()
    private var key: P256.Signing.PrivateKey?
    private var revoked = false

    /// Unique ring identifier. Changes on every fresh-key generation.
    public private(set) var ringId: UUID = UUID()

    /// The UHID identity this ring is bound to.
    public let uhidIdentityId: String

    /// UTC timestamp when this ring was generated.
    public private(set) var generatedAt: Date = Date()

    /// UTC timestamp when this ring was revoked, or `nil` if still active.
    public private(set) var revokedAt: Date?

    /// The DER-encoded public key for this ring (SubjectPublicKeyInfo). Safe to
    /// share; corresponds to the private signing key.
    public private(set) var publicKeyDer: Data = Data()

    private init(uhidIdentityId: String) {
        precondition(!uhidIdentityId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "uhidIdentityId required")
        self.uhidIdentityId = uhidIdentityId
        regenerateKey()
    }

    /// `true` if this ring has been explicitly revoked.
    public var isRevoked: Bool {
        lock.lock(); defer { lock.unlock() }
        return revoked
    }

    /// Creates a new `UhidKeyRing` for `uhidIdentityId` with a freshly generated
    /// P-256 key pair.
    public static func generateFresh(uhidIdentityId: String) -> UhidKeyRing {
        UhidKeyRing(uhidIdentityId: uhidIdentityId)
    }

    /// Rotates the ring: revokes the current key and generates a replacement.
    /// Returns a NEW `UhidKeyRing` — this instance remains revoked.
    public func rotate() -> UhidKeyRing {
        revoke()
        return UhidKeyRing.generateFresh(uhidIdentityId: uhidIdentityId)
    }

    /// Signs `data` with the current private key using ECDSA-SHA256. Throws when
    /// disposed or revoked.
    ///
    /// Signs the SHA-256 digest of `data` directly (via CryptoKit's
    /// `Digest`-typed overload, so the message is hashed exactly once), mirroring
    /// the C# `ECDsa.SignData(data, HashAlgorithmName.SHA256)` semantics.
    public func sign(_ data: Data) throws -> Data {
        lock.lock(); defer { lock.unlock() }
        guard let key else { throw UhidKeyRingError.disposed }
        if revoked {
            throw UhidKeyRingError.revoked(ringId: ringId)
        }
        let digest = SHA256.hash(data: data)
        let signature = try key.signature(for: digest)
        return signature.derRepresentation
    }

    /// Verifies an ECDSA-SHA256 `signature` against `data` using this ring's
    /// public key. Works even after revocation (so prior signatures can still be
    /// validated). Hashes `data` once with SHA-256 to match `sign`.
    public func verify(_ data: Data, signature: Data) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let key else { return false }
        guard let sig = try? P256.Signing.ECDSASignature(derRepresentation: signature) else {
            return false
        }
        let digest = SHA256.hash(data: data)
        return key.publicKey.isValidSignature(sig, for: digest)
    }

    /// Revokes this ring. After revocation `sign` throws; `verify` continues to
    /// work for historical validation. Idempotent.
    public func revoke() {
        lock.lock(); defer { lock.unlock() }
        if revoked { return }
        revoked = true
        revokedAt = Date()
    }

    /// Disposes the ring, releasing its private key. After disposal signing and
    /// verification both fail.
    public func dispose() {
        lock.lock(); defer { lock.unlock() }
        key = nil
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private func regenerateKey() {
        lock.lock(); defer { lock.unlock() }
        let fresh = P256.Signing.PrivateKey()
        key = fresh
        ringId = UUID()
        generatedAt = Date()
        revokedAt = nil
        revoked = false
        publicKeyDer = fresh.publicKey.derRepresentation
    }
}

// MARK: - RedactedEvidenceJsonConverter

/// Redacts `AnomalySignal.evidence` on serialisation: every value is replaced by
/// the hex SHA-256 of its UTF-8 bytes, prefixed with `sha256:`. Keys (evidence
/// labels) are preserved so structured log sinks can still join entries by
/// evidence shape, but the raw values — which may carry session tokens, payload
/// fragments, or PII — never leave the process in clear text.
///
/// Read side intentionally reverses to an empty dictionary: incoming JSON cannot
/// be trusted to carry the original cleartext, and round-tripping hashes back
/// into the dictionary would mask whether the source-of-record is the in-process
/// signal or a serialised copy. This mirrors the C#
/// `JsonConverter<IReadOnlyDictionary<string,string>>.Read` returning empty.
public enum RedactedEvidenceJsonConverter {

    /// Redact a single value to `sha256:<hex-lower>`. An empty/absent value maps
    /// to the bare `sha256:` prefix (matching the C# `HashRedacted` contract).
    public static func hashRedacted(_ raw: String?) -> String {
        guard let raw, !raw.isEmpty else { return "sha256:" }
        let digest = SHA256.hash(data: Data(raw.utf8))
        let hex = digest.map { String(format: "%02x", $0) }.joined()
        return "sha256:" + hex
    }

    /// Produce the redacted `[label: sha256:…]` map for an evidence dictionary
    /// (the "Write" side of the converter). Keys are preserved verbatim; values
    /// are hashed.
    public static func redact(_ evidence: [String: String]) -> [String: String] {
        var out: [String: String] = [:]
        out.reserveCapacity(evidence.count)
        for (k, v) in evidence { out[k] = hashRedacted(v) }
        return out
    }

    /// The "Read" side: incoming JSON evidence is never trusted, so it decodes to
    /// an empty dictionary.
    public static func decodeToEmpty() -> [String: String] { [:] }
}

// MARK: - AnomalySignal : Encodable (redacting evidence)

/// Redacting `Encodable` conformance for `AnomalySignal`. In C# this behaviour
/// comes from the `[JsonConverter(typeof(RedactedEvidenceJsonConverter))]`
/// attribute on the `Evidence` property; in Swift we express the same policy by
/// hand-rolling `encode(to:)` so any JSON serialisation of a signal hashes the
/// evidence values. Field names match the C# record property names (PascalCase),
/// keeping the on-wire shape aligned with the reference serialiser's default
/// naming.
extension AnomalySignal: Encodable {
    private enum CodingKeys: String, CodingKey {
        case id = "Id"
        case vector = "Vector"
        case confidence = "Confidence"
        case affectedModule = "AffectedModule"
        case description = "Description"
        case evidence = "Evidence"
        case detectedAt = "DetectedAt"
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(vector, forKey: .vector)
        try c.encode(confidence, forKey: .confidence)
        try c.encode(affectedModule, forKey: .affectedModule)
        try c.encode(description, forKey: .description)
        // Evidence is REDACTED on write — values become sha256:… hashes.
        try c.encode(RedactedEvidenceJsonConverter.redact(evidence), forKey: .evidence)
        try c.encode(detectedAt, forKey: .detectedAt)
    }
}

// MARK: - ISecurityWatchdog

/// Central contract for the CircleAI local runtime immune system. Receives
/// `AnomalySignal` instances from detection sites and returns the
/// `SecurityResponse` describing protective action taken.
public protocol ISecurityWatchdog: AnyObject, Sendable {
    /// Called by any detection site when a local runtime anomaly is observed.
    /// The watchdog evaluates `signal` and applies the appropriate protective
    /// response.
    ///
    /// - Parameters:
    ///   - signal: The detected anomaly.
    ///   - checkpoint: The most recent `SecurityCheckpoint` for the affected
    ///     module, if one is available. Passed so the watchdog can roll back
    ///     state without holding a reference to it itself.
    func onAnomalyDetected(
        _ signal: AnomalySignal,
        checkpoint: SecurityCheckpoint?
    ) async throws -> SecurityResponse

    /// A live stream of every `AnomalySignal` observed since the watchdog
    /// started. Completes when the caller cancels iteration.
    func streamSignals() -> AsyncStream<AnomalySignal>
}

public extension ISecurityWatchdog {
    /// Overload matching the C# default `checkpoint = null`.
    func onAnomalyDetected(_ signal: AnomalySignal) async throws -> SecurityResponse {
        try await onAnomalyDetected(signal, checkpoint: nil)
    }
}

// MARK: - DefaultSecurityWatchdog

/// Default in-process watchdog. Applies graduated responses based on
/// `ThreatVector` and confidence level:
///   • confidence < 0.30 → `.noAction`
///   • confidence 0.30–0.60 → `.keyRotation`
///   • confidence > 0.60 + confusion/pivot/escalation → `.composite`
///     (rotation + mesh signal), plus `.stateRollback` when a verified
///     checkpoint is available for a high-severity vector.
///
/// In-process broadcast of signals. Single-process correct. Not multi-replica
/// safe — signals emitted on replica A do not reach stream subscribers on
/// replica B (mirrors the C# WireProven note).
public final class DefaultSecurityWatchdog: ISecurityWatchdog, @unchecked Sendable {
    private static let rotationThreshold = 0.30
    private static let compositeThreshold = 0.60

    /// Canonical component name (mirrors the C# `ComponentName`).
    public let componentName = "DefaultSecurityWatchdog"

    // Buffered broadcast of every observed signal.
    private let lock = NSLock()
    private var continuations: [UUID: AsyncStream<AnomalySignal>.Continuation] = [:]
    private var pending: [AnomalySignal] = []

    public init() {}

    public func onAnomalyDetected(
        _ signal: AnomalySignal,
        checkpoint: SecurityCheckpoint? = nil
    ) async throws -> SecurityResponse {
        // Broadcast to any stream subscribers (buffer if none attached yet).
        broadcast(signal)

        // ── Graduated response policy ────────────────────────────────────────

        if Double(signal.confidence) < Self.rotationThreshold {
            return SecurityResponse.noAction(
                signalId: signal.id,
                reason: "Confidence \(Self.pct(signal.confidence)) below rotation threshold — monitoring only.")
        }

        // High-severity vectors always warrant rollback if we have a checkpoint.
        let isHighSeverity =
            signal.vector == .controlFlowDrift ||
            signal.vector == .privilegeEscalation ||
            signal.vector == .networkPivot ||
            signal.vector == .stateCorruption

        if Double(signal.confidence) > Self.compositeThreshold {
            var actions: [SecurityResponseKind] = [.keyRotation, .meshIsolationSignal]

            var restored: SecurityCheckpoint?
            if let checkpoint, isHighSeverity, checkpoint.verify() {
                actions.append(.stateRollback)
                restored = checkpoint
            }

            return SecurityResponse.composite(
                signalId: signal.id,
                actions: actions,
                description: "Composite response for \(Self.vectorName(signal.vector)) " +
                             "(confidence \(Self.pct(signal.confidence))) in \(signal.affectedModule).",
                restoredCheckpoint: restored)
        }

        // Mid-range confidence: rotate keys only.
        return SecurityResponse.forKeyRotation(
            signalId: signal.id,
            description: "Key rotation triggered for \(Self.vectorName(signal.vector)) " +
                         "(confidence \(Self.pct(signal.confidence))) in \(signal.affectedModule).")
    }

    public func streamSignals() -> AsyncStream<AnomalySignal> {
        AsyncStream { continuation in
            let id = UUID()
            lock.lock()
            // Flush pre-subscription signals, then register (unbounded semantics).
            for s in pending { continuation.yield(s) }
            pending.removeAll()
            continuations[id] = continuation
            lock.unlock()
            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock()
                self.continuations[id] = nil
                self.lock.unlock()
            }
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private func broadcast(_ signal: AnomalySignal) {
        lock.lock()
        if continuations.isEmpty {
            pending.append(signal)
            lock.unlock()
            return
        }
        let conts = Array(continuations.values)
        lock.unlock()
        for c in conts { c.yield(signal) }
    }

    /// Renders a [0,1] value as a whole-number percentage with a `%` suffix,
    /// matching .NET's `P0` format for the messages the reference builds.
    static func pct(_ value: Float) -> String {
        let rounded = Int((Double(value) * 100.0).rounded())
        return "\(rounded)%"
    }

    /// PascalCase vector name matching C#'s `ThreatVector.ToString()`.
    static func vectorName(_ v: ThreatVector) -> String {
        switch v {
        case .memoryAnomaly:         return "MemoryAnomaly"
        case .controlFlowDrift:      return "ControlFlowDrift"
        case .privilegeEscalation:   return "PrivilegeEscalation"
        case .biometricSpoofAttempt: return "BiometricSpoofAttempt"
        case .networkPivot:          return "NetworkPivot"
        case .stateCorruption:       return "StateCorruption"
        case .agentPatchRejected:    return "AgentPatchRejected"
        case .unknown:               return "Unknown"
        }
    }
}

// MARK: - IAnomalyEventDispatcher

/// Outcome of a `IAnomalyEventDispatcher.verifyAndDispatch` call. Ordinals follow
/// the C# declaration (explicit values 0–4).
public enum AnomalyDispatchOutcome: Int, Codable, Sendable, CaseIterable {
    /// Signal accepted; watchdog was invoked.
    case dispatched = 0
    /// Signal id was already seen — deduped silently.
    case duplicate = 1
    /// Confidence was below the configured threshold — ignored.
    case belowThreshold = 2
    /// Signal failed the origin/signature verification step.
    case unverified = 3
    /// Cancellation was requested before dispatch.
    case cancelled = 4
}

/// Result of a dispatch attempt.
public struct AnomalyDispatchResult: Sendable {
    /// What the dispatcher did with the signal.
    public let outcome: AnomalyDispatchOutcome
    /// The watchdog response, when `outcome` is `.dispatched`. `nil` otherwise.
    public let response: SecurityResponse?

    public init(outcome: AnomalyDispatchOutcome, response: SecurityResponse?) {
        self.outcome = outcome
        self.response = response
    }
}

/// Verify, dedup, and dispatch an `AnomalySignal` in a single call. Returns an
/// `AnomalyDispatchResult` describing what happened — no error is thrown on
/// rejection so the caller can branch on the outcome without a do/catch.
public protocol IAnomalyEventDispatcher: AnyObject, Sendable {
    /// Runs the verification pipeline configured on this dispatcher (origin
    /// trust, optional signature check, confidence threshold) and, when all gates
    /// pass, hands the signal to the wrapped `ISecurityWatchdog`. Returns the
    /// dispatch outcome along with the watchdog response if invocation was
    /// reached.
    ///
    /// `cancelled` reflects a caller-provided cancellation flag; Swift structured
    /// concurrency has no ambient `CancellationToken`, so the flag is passed
    /// explicitly (default `false`).
    func verifyAndDispatch(
        _ signal: AnomalySignal,
        checkpoint: SecurityCheckpoint?,
        isCancelled: Bool
    ) async throws -> AnomalyDispatchResult
}

public extension IAnomalyEventDispatcher {
    /// Overload matching the C# defaults (`checkpoint = null`, no cancellation).
    func verifyAndDispatch(
        _ signal: AnomalySignal,
        checkpoint: SecurityCheckpoint? = nil
    ) async throws -> AnomalyDispatchResult {
        try await verifyAndDispatch(signal, checkpoint: checkpoint, isCancelled: false)
    }
}

// MARK: - DefaultAnomalyEventDispatcher

/// Default in-process dispatcher. Threshold-gated, id-deduped, no signature
/// verification (compose with your own signature-verifying wrapper when running
/// over an untrusted transport).
public final class DefaultAnomalyEventDispatcher: IAnomalyEventDispatcher, @unchecked Sendable {
    private let watchdog: ISecurityWatchdog
    private let minimumConfidence: Double

    private let lock = NSLock()
    private var seen: Set<UUID> = []

    /// Creates the dispatcher.
    ///
    /// - Parameters:
    ///   - watchdog: The watchdog to forward verified signals to.
    ///   - minimumConfidence: Drop signals whose `AnomalySignal.confidence` is
    ///     below this value. Default 0.30 — matches the default watchdog rotation
    ///     threshold so signals that would have been no-ops aren't even
    ///     dispatched. Clamped into [0, 1].
    public init(watchdog: ISecurityWatchdog, minimumConfidence: Double = 0.30) {
        self.watchdog = watchdog
        self.minimumConfidence = securityClamp(minimumConfidence, 0.0, 1.0)
    }

    public func verifyAndDispatch(
        _ signal: AnomalySignal,
        checkpoint: SecurityCheckpoint? = nil,
        isCancelled: Bool = false
    ) async throws -> AnomalyDispatchResult {
        if isCancelled {
            return AnomalyDispatchResult(outcome: .cancelled, response: nil)
        }

        if Double(signal.confidence) < minimumConfidence {
            return AnomalyDispatchResult(outcome: .belowThreshold, response: nil)
        }

        // Atomic "add if absent" — dedup by signal id.
        lock.lock()
        let inserted = seen.insert(signal.id).inserted
        lock.unlock()
        if !inserted {
            return AnomalyDispatchResult(outcome: .duplicate, response: nil)
        }

        let response = try await watchdog.onAnomalyDetected(signal, checkpoint: checkpoint)
        return AnomalyDispatchResult(outcome: .dispatched, response: response)
    }
}
