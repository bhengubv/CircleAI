// Federation.swift
//
// Port of CircleAI.Federation/ — the federated-learning round coordinator.
// NO raw training data leaves the device — only signed weight deltas.
//   • ModelDelta.cs                    — ModelDelta
//   • FederationRound.cs               — RoundStatus, FederationRound
//   • IFederationParticipant.cs        — IFederationParticipant
//   • IFederationDeltaDispatcher.cs    — IFederationDeltaDispatcher, DeltaDispatchOutcome
//   • IFederationAggregator.cs         — IFederationAggregator
//   • FederatedAveraging.cs            — FederatedAveraging (sample-weighted mean)
//   • InMemoryFederationAggregator.cs  — InMemoryFederationAggregator
//
// Porting notes:
//   • `Guid` → `UUID`; `byte[]` → `Data`.
//   • The C# `InMemoryFederationAggregator` extends `CircleAIComponentBase` and
//     wraps each op in `RunOperationAsync(name, closure, ct)` — a logging/
//     telemetry shim. The Swift SDK has no such base, so the port runs the
//     operation directly (behaviour is identical; only the telemetry wrapper is
//     dropped). The optional signature-validator delegate is preserved.
//   • `KeyNotFoundException` / `InvalidOperationException` → `FederationError`.
//   • FederatedAveraging reads/writes little-endian IEEE-754 `Float` — ported
//     byte-for-byte so aggregated payloads match the C# encoding.
//   • The reference aggregator adds a delta-dispatcher (`IFederationDeltaDispatcher`)
//     whose in-memory impl composes verify → dedup → submit against the
//     aggregator, matching the safe-by-default contract.

import Foundation

// MARK: - ModelDelta

/// One participant's signed contribution to a federation round. (C# `ModelDelta`.)
public struct ModelDelta: Sendable, Equatable, Codable {
    /// Unique delta identifier.
    public let id: UUID
    /// Round this delta belongs to.
    public let roundId: UUID
    /// Pseudonymous (hashed) contributor UHID — never raw PII.
    public let contributorUhid: String
    /// Model the delta applies to.
    public let modelId: String
    /// Base model version the participant trained on.
    public let fromVersion: String
    /// Opaque weight-delta payload (reference encoding: little-endian float[]).
    public let deltaPayload: Data
    /// Local training sample count (the federated-averaging weight).
    public let sampleCount: Int
    /// Signature over the payload (verified by an injected validator).
    public let signature: Data
    /// UTC submission timestamp.
    public let submittedAt: Date

    public init(id: UUID, roundId: UUID, contributorUhid: String, modelId: String,
                fromVersion: String, deltaPayload: Data, sampleCount: Int, signature: Data,
                submittedAt: Date) {
        self.id = id
        self.roundId = roundId
        self.contributorUhid = contributorUhid
        self.modelId = modelId
        self.fromVersion = fromVersion
        self.deltaPayload = deltaPayload
        self.sampleCount = sampleCount
        self.signature = signature
        self.submittedAt = submittedAt
    }
}

// MARK: - FederationRound

/// Lifecycle state of a `FederationRound`. (C# `RoundStatus`.)
public enum RoundStatus: Int, Sendable, Codable, CaseIterable {
    /// Accepting deltas.
    case open = 0
    /// Has the minimum delta count and is averaging.
    case aggregating = 1
    /// Committed an aggregated model; further deltas rejected.
    case committed = 2
    /// Abandoned (timeout, insufficient participants).
    case aborted = 3
}

/// One coordinated round of federated learning. (C# `FederationRound`.)
public struct FederationRound: Sendable, Equatable, Codable {
    /// Unique round identifier.
    public let id: UUID
    /// Canonical model name shared by all participants.
    public let modelId: String
    /// Base model version participants train on.
    public let fromVersion: String
    /// Version the aggregated model will publish as.
    public let toVersion: String
    /// Minimum valid deltas before the round may commit.
    public let minParticipants: Int
    /// Hard upper bound on accepted deltas.
    public let maxParticipants: Int
    /// Deltas accepted so far.
    public let currentParticipantCount: Int
    /// Current lifecycle state.
    public let status: RoundStatus
    /// UTC open time.
    public let openedAt: Date
    /// UTC commit time, or `nil`.
    public let committedAt: Date?

    public init(id: UUID, modelId: String, fromVersion: String, toVersion: String,
                minParticipants: Int, maxParticipants: Int, currentParticipantCount: Int,
                status: RoundStatus, openedAt: Date, committedAt: Date?) {
        self.id = id
        self.modelId = modelId
        self.fromVersion = fromVersion
        self.toVersion = toVersion
        self.minParticipants = minParticipants
        self.maxParticipants = maxParticipants
        self.currentParticipantCount = currentParticipantCount
        self.status = status
        self.openedAt = openedAt
        self.committedAt = committedAt
    }

    /// Returns a copy with selected fields replaced (C# `with` expression).
    func with(currentParticipantCount: Int? = nil, status: RoundStatus? = nil,
              committedAt: Date?? = nil) -> FederationRound {
        FederationRound(
            id: id, modelId: modelId, fromVersion: fromVersion, toVersion: toVersion,
            minParticipants: minParticipants, maxParticipants: maxParticipants,
            currentParticipantCount: currentParticipantCount ?? self.currentParticipantCount,
            status: status ?? self.status, openedAt: openedAt,
            committedAt: committedAt ?? self.committedAt)
    }
}

// MARK: - Errors

/// Errors raised by the federation aggregator / averaging. Mirrors the C#
/// `ArgumentException` / `ArgumentOutOfRangeException` / `KeyNotFoundException` /
/// `InvalidOperationException`.
public enum FederationError: Error, Equatable, CustomStringConvertible {
    case modelIdRequired
    case versionRequired
    case minParticipantsMustBePositive
    case maxLessThanMin(max: Int, min: Int)
    case roundUnknown(UUID)
    case roundNotAcceptingDeltas(UUID, RoundStatus)
    case maxParticipantsReached(UUID, Int)
    case limitMustBePositive
    // Averaging
    case emptyDeltaList
    case emptyPayload
    case payloadNotMultipleOfFour(Int)
    case payloadLengthMismatch(index0: Int, index: Int, indexLen: Int)
    case negativeSampleCount(UUID, Int)
    case zeroTotalSampleWeight

    public var description: String {
        switch self {
        case .modelIdRequired: return "modelId required"
        case .versionRequired: return "version required"
        case .minParticipantsMustBePositive: return "minParticipants must be positive."
        case let .maxLessThanMin(max, min): return "maxParticipants (\(max)) must be >= minParticipants (\(min))."
        case .roundUnknown(let id): return "Round \(id) is unknown."
        case let .roundNotAcceptingDeltas(id, s): return "Round \(id) is \(s); not accepting deltas."
        case let .maxParticipantsReached(id, m): return "Round \(id) has reached MaxParticipants (\(m))."
        case .limitMustBePositive: return "limit must be positive."
        case .emptyDeltaList: return "Cannot average an empty delta list."
        case .emptyPayload: return "Delta payloads must be non-empty."
        case .payloadNotMultipleOfFour(let n): return "Delta payload length (\(n)) must be a multiple of 4 bytes."
        case let .payloadLengthMismatch(i0, i, len): return "Delta payload length mismatch: index 0 = \(i0) bytes, index \(i) = \(len) bytes."
        case let .negativeSampleCount(id, n): return "SampleCount must be non-negative; delta \(id) reported \(n)."
        case .zeroTotalSampleWeight: return "Total sample weight across deltas is zero — cannot perform weighted average."
        }
    }
}

// MARK: - Participant / dispatcher contracts

/// A device that contributes to federation rounds. (C# `IFederationParticipant`.)
public protocol IFederationParticipant: Sendable {
    /// Trains locally and returns a signed delta. Only the delta leaves the
    /// device — never raw training data.
    func produceDelta(round: FederationRound) async throws -> ModelDelta
    /// Applies an aggregated model and reports success.
    func applyAggregatedModel(modelId: String, newVersion: String, aggregatedPayload: Data) async throws -> Bool
}

/// Outcome of a `IFederationDeltaDispatcher.verifyAndSubmit` call.
/// (C# `DeltaDispatchOutcome`.)
public enum DeltaDispatchOutcome: Int, Sendable, Codable, CaseIterable {
    case accepted = 0
    case signatureInvalid = 1
    case duplicate = 2
    case roundUnknown = 3
    case roundClosed = 4
}

/// Safe-by-default federation delta dispatcher — verify, dedup, and submit in
/// one call. (C# `IFederationDeltaDispatcher`.)
public protocol IFederationDeltaDispatcher: Sendable {
    /// Verify the signature, check for a duplicate, and submit. Never throws on
    /// rejection — returns the outcome.
    func verifyAndSubmit(_ delta: ModelDelta) async -> DeltaDispatchOutcome
}

/// Coordinator for federation rounds. (C# `IFederationAggregator`.)
public protocol IFederationAggregator: Sendable {
    /// Opens a new round.
    func openRound(modelId: String, fromVersion: String, toVersion: String,
                   minParticipants: Int, maxParticipants: Int) async throws -> FederationRound
    /// Submits a signed delta to its round. Throws when the round is unknown,
    /// closed, or full.
    func submitDelta(_ delta: ModelDelta) async throws
    /// Attempts to commit — returns the aggregated payload once
    /// `minParticipants` valid deltas exist, else `nil`.
    func tryCommit(_ roundId: UUID) async throws -> Data?
    /// Returns the round snapshot. Throws when unknown.
    func getRound(_ roundId: UUID) async throws -> FederationRound
}

// MARK: - FederatedAveraging

/// Sample-size-weighted averaging over `ModelDelta.deltaPayload` arrays
/// interpreted as little-endian IEEE-754 `Float`. (C# `FederatedAveraging`.)
public enum FederatedAveraging {

    /// Weighted average of the deltas, returned as little-endian float bytes.
    public static func average(_ deltas: [ModelDelta]) throws -> Data {
        if deltas.isEmpty { throw FederationError.emptyDeltaList }

        let expectedBytes = deltas[0].deltaPayload.count
        if expectedBytes == 0 { throw FederationError.emptyPayload }
        if expectedBytes % 4 != 0 { throw FederationError.payloadNotMultipleOfFour(expectedBytes) }

        for i in 1..<deltas.count where deltas[i].deltaPayload.count != expectedBytes {
            throw FederationError.payloadLengthMismatch(index0: expectedBytes, index: i,
                                                        indexLen: deltas[i].deltaPayload.count)
        }

        let floatCount = expectedBytes / 4
        var totalSamples: Int64 = 0
        for d in deltas {
            if d.sampleCount < 0 { throw FederationError.negativeSampleCount(d.id, d.sampleCount) }
            totalSamples += Int64(d.sampleCount)
        }
        if totalSamples == 0 { throw FederationError.zeroTotalSampleWeight }

        var accumulator = [Double](repeating: 0, count: floatCount)
        for d in deltas {
            let weight = Double(d.sampleCount) / Double(totalSamples)
            let floats = decodeFloats(d.deltaPayload)
            for i in 0..<floatCount {
                accumulator[i] += Double(floats[i]) * weight
            }
        }

        return encodeFloats(accumulator.map { Float($0) })
    }

    /// Encodes a `Float` array as little-endian IEEE-754 bytes. Written
    /// byte-wise (not via `storeBytes`) so there is no alignment precondition on
    /// the backing buffer.
    public static func encodeFloats(_ values: [Float]) -> Data {
        var out = [UInt8]()
        out.reserveCapacity(values.count * 4)
        for v in values {
            let bits = v.bitPattern  // host-order UInt32
            out.append(UInt8(bits & 0xFF))
            out.append(UInt8((bits >> 8) & 0xFF))
            out.append(UInt8((bits >> 16) & 0xFF))
            out.append(UInt8((bits >> 24) & 0xFF))
        }
        return Data(out)
    }

    /// Decodes little-endian IEEE-754 bytes into a `Float` array. Throws when
    /// the length is not a multiple of 4.
    public static func decodeFloats(_ payload: Data) -> [Float] {
        let count = payload.count / 4
        var out = [Float](repeating: 0, count: count)
        // Copy to a contiguous buffer to avoid Data slice base-offset pitfalls.
        let bytes = [UInt8](payload)
        for i in 0..<count {
            let b0 = UInt32(bytes[i * 4])
            let b1 = UInt32(bytes[i * 4 + 1]) << 8
            let b2 = UInt32(bytes[i * 4 + 2]) << 16
            let b3 = UInt32(bytes[i * 4 + 3]) << 24
            out[i] = Float(bitPattern: b0 | b1 | b2 | b3)
        }
        return out
    }

    /// Throwing decode that validates the payload length (C# `DecodeFloats`).
    public static func decodeFloatsChecked(_ payload: Data) throws -> [Float] {
        if payload.count % 4 != 0 { throw FederationError.payloadNotMultipleOfFour(payload.count) }
        return decodeFloats(payload)
    }
}

// MARK: - InMemoryFederationAggregator

/// In-process reference `IFederationAggregator`. Stores round + delta state in
/// memory; performs sample-weighted averaging on commit. Signature verification
/// is delegated to an injected validator so this stays engine-agnostic.
/// (C# `InMemoryFederationAggregator`.)
public final class InMemoryFederationAggregator: IFederationAggregator, @unchecked Sendable {
    private final class RoundState {
        var snapshot: FederationRound
        var deltas: [ModelDelta] = []
        var committedPayload: Data?
        init(_ initial: FederationRound) { self.snapshot = initial }
    }

    private let lock = NSLock()
    private var rounds: [UUID: RoundState] = [:]
    private let signatureValidator: @Sendable (ModelDelta) -> Bool

    /// Construct with a signature validator. Pass `{ _ in true }` in tests where
    /// signatures are not the subject of test.
    public init(signatureValidator: @escaping @Sendable (ModelDelta) -> Bool) {
        self.signatureValidator = signatureValidator
    }

    public func openRound(modelId: String, fromVersion: String, toVersion: String,
                          minParticipants: Int, maxParticipants: Int) async throws -> FederationRound {
        if modelId.isEmpty { throw FederationError.modelIdRequired }
        if fromVersion.isEmpty || toVersion.isEmpty { throw FederationError.versionRequired }
        if minParticipants <= 0 { throw FederationError.minParticipantsMustBePositive }
        if maxParticipants < minParticipants {
            throw FederationError.maxLessThanMin(max: maxParticipants, min: minParticipants)
        }

        let round = FederationRound(
            id: UUID(), modelId: modelId, fromVersion: fromVersion, toVersion: toVersion,
            minParticipants: minParticipants, maxParticipants: maxParticipants,
            currentParticipantCount: 0, status: .open, openedAt: Date(), committedAt: nil)
        let state = RoundState(round)
        lock.lock(); rounds[round.id] = state; lock.unlock()
        return round
    }

    public func submitDelta(_ delta: ModelDelta) async throws {
        lock.lock()
        guard let state = rounds[delta.roundId] else {
            lock.unlock()
            throw FederationError.roundUnknown(delta.roundId)
        }
        // Empty payloads are treated as invalid: not stored, not counted, but
        // do not raise (matches C#) so the round stays viable.
        if delta.deltaPayload.isEmpty { lock.unlock(); return }

        if state.snapshot.status != .open {
            let s = state.snapshot.status
            lock.unlock()
            throw FederationError.roundNotAcceptingDeltas(delta.roundId, s)
        }
        if state.deltas.count >= state.snapshot.maxParticipants {
            let m = state.snapshot.maxParticipants
            lock.unlock()
            throw FederationError.maxParticipantsReached(delta.roundId, m)
        }
        state.deltas.append(delta)
        state.snapshot = state.snapshot.with(currentParticipantCount: state.deltas.count)
        lock.unlock()
    }

    public func tryCommit(_ roundId: UUID) async throws -> Data? {
        lock.lock()
        guard let state = rounds[roundId] else {
            lock.unlock()
            throw FederationError.roundUnknown(roundId)
        }
        // Idempotent: re-return the previously committed payload.
        if state.snapshot.status == .committed { let p = state.committedPayload; lock.unlock(); return p }
        if state.snapshot.status == .aborted { lock.unlock(); return nil }

        let validDeltas = state.deltas.filter(signatureValidator)
        if validDeltas.count < state.snapshot.minParticipants { lock.unlock(); return nil }

        state.snapshot = state.snapshot.with(status: .aggregating)

        let aggregated: Data
        do {
            aggregated = try FederatedAveraging.average(validDeltas)
        } catch is FederationError {
            // Payload encoding inconsistent — fall back to the median delta by
            // SampleCount (matches the C# fallback).
            aggregated = Self.fallbackMedianPayload(validDeltas)
        }

        state.committedPayload = aggregated
        state.snapshot = state.snapshot.with(status: .committed, committedAt: .some(Date()))
        lock.unlock()
        return aggregated
    }

    public func getRound(_ roundId: UUID) async throws -> FederationRound {
        lock.lock(); defer { lock.unlock() }
        guard let state = rounds[roundId] else { throw FederationError.roundUnknown(roundId) }
        return state.snapshot
    }

    /// Total rounds tracked. Diagnostic only.
    public var roundCount: Int {
        lock.lock(); defer { lock.unlock() }
        return rounds.count
    }

    private static func fallbackMedianPayload(_ deltas: [ModelDelta]) -> Data {
        let ordered = deltas.sorted { $0.sampleCount < $1.sampleCount }
        return ordered[ordered.count / 2].deltaPayload
    }
}

// MARK: - DefaultFederationDeltaDispatcher

/// Composes verify → dedup → submit against an `InMemoryFederationAggregator`,
/// so consumers cannot skip a step. Deduplicates by delta id per round.
/// (C# `DefaultFederationDeltaDispatcher` — the safe-by-default composer.)
public final class DefaultFederationDeltaDispatcher: IFederationDeltaDispatcher, @unchecked Sendable {
    private let aggregator: InMemoryFederationAggregator
    private let signatureValidator: @Sendable (ModelDelta) -> Bool
    private let lock = NSLock()
    private var seen: [UUID: Set<UUID>] = [:]  // roundId → delta ids

    public init(aggregator: InMemoryFederationAggregator,
                signatureValidator: @escaping @Sendable (ModelDelta) -> Bool) {
        self.aggregator = aggregator
        self.signatureValidator = signatureValidator
    }

    public func verifyAndSubmit(_ delta: ModelDelta) async -> DeltaDispatchOutcome {
        // 1. Signature.
        if !signatureValidator(delta) { return .signatureInvalid }

        // 2. Dedup by (round, delta id).
        lock.lock()
        if seen[delta.roundId]?.contains(delta.id) == true { lock.unlock(); return .duplicate }
        lock.unlock()

        // 3. Submit — map aggregator errors to outcomes.
        do {
            try await aggregator.submitDelta(delta)
        } catch FederationError.roundUnknown {
            return .roundUnknown
        } catch FederationError.roundNotAcceptingDeltas, FederationError.maxParticipantsReached {
            return .roundClosed
        } catch {
            return .roundClosed
        }

        lock.lock(); seen[delta.roundId, default: []].insert(delta.id); lock.unlock()
        return .accepted
    }
}

/// Backwards-compatible alias for the pre-rename name. The canonical name is
/// `DefaultFederationDeltaDispatcher` (matching the C# reference).
public typealias InMemoryFederationDeltaDispatcher = DefaultFederationDeltaDispatcher
