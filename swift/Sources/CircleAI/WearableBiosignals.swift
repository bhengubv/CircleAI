// WearableBiosignals.swift
//
// Port of the Wearable.Biosignals module from
// src/CircleAI.Wearable.Biosignals/*.cs — the biosignal streaming layer:
//   • BiosignalKind                         — stable-integer taxonomy (0..8)
//   • BiosignalSample (+ Create factory)    — a single measurement
//   • IBiosignalSource                      — streaming source contract
//   • NullBiosignalSource                   — no-op source
//   • RecordedBiosignalSource               — deterministic replay source
//   • BiosignalStats / BiosignalSnapshot    — sliding-window aggregates
//   • BiosignalAggregator                   — single-shot windowed snapshot
//   • BiosignalAffectMapper                 — deterministic affect projection
//
// Porting notes:
//   • `BiosignalKind` keeps its explicit integer values (Int-backed, stable
//     across ports — do not renumber).
//   • `Guid` → `UUID`; `float` → `Float`; `DateTimeOffset` → `Date`;
//     `IAsyncEnumerable<BiosignalSample>` → `AsyncThrowingStream<BiosignalSample, Error>`.
//   • `RecordedBiosignalSource` replays samples (optionally with a per-sample
//     delay) and honours cancellation. Subscribers consume via `for try await`.
//   • `BiosignalAggregator.snapshot(window:)` is a single-shot windowed read:
//     it time-bounds the source read to `window` and aggregates samples whose
//     `measuredAt >= now - window`. `window <= 0` throws `.windowMustBePositive`.
//   • `BiosignalAffectMapper.apply` mutates an `AffectState` in place using the
//     fixture rule sheet; low-confidence samples (< 0.5) never mutate.
//   • The C# `[Experimental]` / `[CircleAIVerificationStatus(Reference)]`
//     attributes are C#-only metadata and are intentionally not carried across.

import Foundation

// MARK: - BiosignalKind

/// Canonical kinds of biosignal samples Circle AI consumes from wearables.
/// Integer values are stable across language ports — do not renumber.
public enum BiosignalKind: Int, Sendable, Equatable, Codable, CaseIterable {
    /// Heart rate, beats per minute.
    case heartRate = 0
    /// Heart rate variability, RMSSD in milliseconds.
    case heartRateVariability = 1
    /// Peripheral oxygen saturation, percent (0-100).
    case oxygenSaturation = 2
    /// Accelerometer magnitude, m/s^2.
    case accelerometer = 3
    /// Body temperature, degrees Celsius.
    case bodyTemperature = 4
    /// Sleep stage encoded as a float: 0=awake, 1=light, 2=deep, 3=REM.
    case sleepStage = 5
    /// Step count (cumulative or delta — see `BiosignalSample.isCumulative`).
    case steps = 6
    /// Galvanic skin response, microsiemens.
    case galvanicSkinResponse = 7
    /// Catch-all for vendor-specific or future signals.
    case unknown = 8
}

// MARK: - BiosignalSample

/// A single biosignal measurement.
public struct BiosignalSample: Sendable, Equatable, Codable {
    /// Stable identifier for this sample.
    public let id: UUID
    /// The kind of signal.
    public let kind: BiosignalKind
    /// Numeric value in the canonical unit for the kind.
    public let value: Float
    /// Canonical unit string ("bpm", "ms", "%", "m/s^2", "celsius", "stage", "count", "uS").
    public let unit: String
    /// Sensor-reported confidence in [0, 1]. Samples below 0.5 are typically ignored by the mapper.
    public let confidence: Float
    /// True when `kind` is `.steps` and the value is total-since-epoch rather than a delta.
    public let isCumulative: Bool
    /// UTC time the sample was captured.
    public let measuredAt: Date

    public init(id: UUID, kind: BiosignalKind, value: Float, unit: String, confidence: Float, isCumulative: Bool, measuredAt: Date) {
        self.id = id
        self.kind = kind
        self.value = value
        self.unit = unit
        self.confidence = confidence
        self.isCumulative = isCumulative
        self.measuredAt = measuredAt
    }

    /// Creates a fresh sample with a new `UUID` id, current UTC timestamp, and
    /// confidence clamped to [0, 1].
    public static func create(kind: BiosignalKind, value: Float, unit: String, confidence: Float = 1.0, isCumulative: Bool = false) -> BiosignalSample {
        BiosignalSample(
            id: UUID(),
            kind: kind,
            value: value,
            unit: unit,
            confidence: min(max(confidence, 0), 1),
            isCumulative: isCumulative,
            measuredAt: Date())
    }
}

// MARK: - IBiosignalSource

/// A streaming source of biosignal samples — a wearable, a platform health API,
/// or a simulator for tests.
public protocol IBiosignalSource: Sendable {
    /// The kinds of signals this source can emit. May be empty for the null source.
    var supportedKinds: [BiosignalKind] { get }

    /// Streams biosignal samples until the task is cancelled or the source completes.
    func stream() -> AsyncThrowingStream<BiosignalSample, Error>

    /// Reports whether this source can produce samples of the given kind.
    func isSupported(_ kind: BiosignalKind) async -> Bool
}

// MARK: - NullBiosignalSource

/// A biosignal source that supports nothing and emits nothing. Use for tests and
/// as the default when no wearable is connected.
public final class NullBiosignalSource: IBiosignalSource, @unchecked Sendable {
    public init() {}

    public var supportedKinds: [BiosignalKind] { [] }

    public func isSupported(_ kind: BiosignalKind) async -> Bool { false }

    public func stream() -> AsyncThrowingStream<BiosignalSample, Error> {
        AsyncThrowingStream { continuation in
            continuation.finish()
        }
    }
}

// MARK: - RecordedBiosignalSource

/// Replays a recorded biosignal stream. Useful for tests, training data, and host
/// integration when no live wearable is connected.
public final class RecordedBiosignalSource: IBiosignalSource, @unchecked Sendable {
    private let samples: [BiosignalSample]
    private let kinds: [BiosignalKind]
    private let replayDelay: TimeInterval

    public init(samples: [BiosignalSample], replayDelay: TimeInterval = 0) {
        self.samples = samples
        self.replayDelay = replayDelay
        var seen = Set<BiosignalKind>()
        for s in samples { seen.insert(s.kind) }
        self.kinds = Array(seen)
    }

    public var supportedKinds: [BiosignalKind] { kinds }

    public func isSupported(_ kind: BiosignalKind) async -> Bool { kinds.contains(kind) }

    public func stream() -> AsyncThrowingStream<BiosignalSample, Error> {
        let samples = self.samples
        let delay = self.replayDelay
        return AsyncThrowingStream { continuation in
            let task = Task {
                for s in samples {
                    if Task.isCancelled { continuation.finish(throwing: CancellationError()); return }
                    if delay > 0 {
                        do {
                            try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
                        } catch {
                            continuation.finish(throwing: CancellationError()); return
                        }
                    }
                    continuation.yield(s)
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}

// MARK: - Aggregates

/// Per-kind aggregate statistics over a sliding window.
public struct BiosignalStats: Sendable, Equatable, Codable {
    public let sampleCount: Int
    public let min: Float
    public let max: Float
    public let mean: Float

    public init(sampleCount: Int, min: Float, max: Float, mean: Float) {
        self.sampleCount = sampleCount
        self.min = min
        self.max = max
        self.mean = mean
    }
}

/// A snapshot of biosignal aggregates across all observed kinds at a point in time.
public struct BiosignalSnapshot: Sendable, Equatable, Codable {
    public let stats: [BiosignalKind: BiosignalStats]
    public let generatedAt: Date

    public init(stats: [BiosignalKind: BiosignalStats], generatedAt: Date) {
        self.stats = stats
        self.generatedAt = generatedAt
    }
}

// MARK: - BiosignalAggregator

/// Sliding-window aggregator over an `IBiosignalSource`.
public final class BiosignalAggregator: @unchecked Sendable {
    private let source: IBiosignalSource

    public init(source: IBiosignalSource) {
        self.source = source
    }

    /// Consumes samples from the source until either the source completes or the
    /// total elapsed time exceeds `window`, then returns a snapshot over the
    /// samples that fell within the window (relative to UTC now at call time).
    ///
    /// The read is time-bound: the consuming task races a `window`-length timeout
    /// so a never-completing source still yields a snapshot. Cancelling the
    /// consumer also cancels the underlying stream via its `onTermination` hook.
    public func snapshot(window: TimeInterval) async throws -> BiosignalSnapshot {
        if window <= 0 { throw BiosignalError.windowMustBePositive }

        let generatedAt = Date()
        let cutoff = generatedAt.addingTimeInterval(-window)
        let deadline = generatedAt.addingTimeInterval(window)
        let stream = source.stream()

        // Mirror the C# `SnapshotAsync`: consume the source, accumulating samples
        // whose `measuredAt >= cutoff`, and time-bound the read to `window` so a
        // never-completing source still yields a snapshot (the C# uses
        // `cts.CancelAfter(window)`). A self-completing source (recorded /
        // synthetic) simply ends the stream and we return what it accumulated.
        //
        // The accumulator is shared and lock-guarded rather than returned from a
        // racing child task: the result must be whatever was accumulated within the
        // window, independent of whether the read finished on its own or was cut
        // short by the timeout — never discarded by which task the group surfaces
        // first.
        let store = AccumulatorStore()

        await withTaskGroup(of: Void.self) { group in
            group.addTask {
                do {
                    for try await sample in stream {
                        if Task.isCancelled { break }
                        if sample.measuredAt < cutoff { continue }
                        store.add(kind: sample.kind, value: sample.value)
                        if Date() >= deadline { break }
                    }
                } catch {
                    // Cancellation (window elapsed) or a source error — fall through
                    // with whatever was accumulated so far, exactly as the C#
                    // `catch (OperationCanceledException)` does.
                }
            }
            group.addTask {
                // Timeout safety-net: bound a non-completing source to `window`.
                try? await Task.sleep(nanoseconds: UInt64(window * 1_000_000_000))
            }
            // The first child to finish is the read (self-completing sources) or the
            // timeout (infinite sources). Cancel the rest and drain.
            _ = await group.next()
            group.cancelAll()
        }

        return BiosignalSnapshot(stats: store.snapshot(), generatedAt: generatedAt)
    }

    /// Per-kind running min/max/mean/count. Mirrors the C# private `Accumulator`.
    private struct Accumulator {
        private var count = 0
        private var minV: Float = .infinity
        private var maxV: Float = -.infinity
        private var sum: Double = 0

        mutating func add(_ v: Float) {
            count += 1
            if v < minV { minV = v }
            if v > maxV { maxV = v }
            sum += Double(v)
        }

        func toStats() -> BiosignalStats {
            BiosignalStats(sampleCount: count, min: minV, max: maxV, mean: count == 0 ? 0 : Float(sum / Double(count)))
        }
    }

    /// Lock-guarded per-kind accumulator shared between the stream-reading child
    /// task and the caller. `@unchecked Sendable` because all access to the
    /// mutable dictionary is serialised by `lock`.
    private final class AccumulatorStore: @unchecked Sendable {
        private let lock = NSLock()
        private var byKind: [BiosignalKind: Accumulator] = [:]

        func add(kind: BiosignalKind, value: Float) {
            lock.lock(); defer { lock.unlock() }
            byKind[kind, default: Accumulator()].add(value)
        }

        /// Materialise the current per-kind statistics.
        func snapshot() -> [BiosignalKind: BiosignalStats] {
            lock.lock(); defer { lock.unlock() }
            var out: [BiosignalKind: BiosignalStats] = [:]
            out.reserveCapacity(byKind.count)
            for (kind, acc) in byKind { out[kind] = acc.toStats() }
            return out
        }
    }
}

// MARK: - Errors

public enum BiosignalError: Error, Equatable, CustomStringConvertible {
    case windowMustBePositive

    public var description: String {
        switch self {
        case .windowMustBePositive: return "Window must be positive."
        }
    }
}

// MARK: - BiosignalAffectMapper

/// Maps biosignal samples to `AffectState` mutations using deterministic,
/// fixture-validated rules. Mutates the passed `AffectState` in place. All
/// resulting field values are clamped to [0, 1].
///
/// Rule sheet:
///   • HeartRate > 130 bpm (conf ≥ 0.5): energy += 0.10, uncertainty += 0.05.
///   • HeartRate > 100 bpm (conf ≥ 0.5): energy += 0.05.
///   • HeartRate < 50 bpm  (conf ≥ 0.5): energy -= 0.05.
///   • HRV < 20 ms (conf ≥ 0.5): uncertainty += 0.05, rapport -= 0.02.
///   • HRV > 60 ms (conf ≥ 0.5): engagement += 0.02.
///   • SpO2 < 90 % (conf ≥ 0.5): uncertainty += 0.10.
///   • SleepStage: no mutation. Confidence < 0.5: no mutation.
public enum BiosignalAffectMapper {
    private static let minConfidence: Float = 0.5

    /// Applies the rule for `sample` to `affect`.
    public static func apply(_ sample: BiosignalSample, to affect: AffectState) {
        // Confidence gate — low-confidence samples never mutate state.
        if sample.confidence < minConfidence { return }

        switch sample.kind {
        case .heartRate:
            applyHeartRate(sample.value, affect)
        case .heartRateVariability:
            applyHrv(sample.value, affect)
        case .oxygenSaturation:
            applySpO2(sample.value, affect)
        case .sleepStage:
            // Deep/REM/awake/light — sleep itself is not affect; do nothing.
            break
        default:
            // Accelerometer, temperature, steps, GSR, unknown — no rule yet.
            break
        }

        affect.lastUpdatedAt = Date()
    }

    private static func applyHeartRate(_ bpm: Float, _ a: AffectState) {
        if bpm > 130 {
            a.energy = clamp01(a.energy + 0.10)
            a.uncertainty = clamp01(a.uncertainty + 0.05)
        } else if bpm > 100 {
            a.energy = clamp01(a.energy + 0.05)
        } else if bpm < 50 {
            a.energy = clamp01(a.energy - 0.05)
        }
    }

    private static func applyHrv(_ rmssdMs: Float, _ a: AffectState) {
        if rmssdMs < 20 {
            a.uncertainty = clamp01(a.uncertainty + 0.05)
            a.rapport = clamp01(a.rapport - 0.02)
        } else if rmssdMs > 60 {
            a.engagement = clamp01(a.engagement + 0.02)
        }
    }

    private static func applySpO2(_ percent: Float, _ a: AffectState) {
        if percent < 90 {
            a.uncertainty = clamp01(a.uncertainty + 0.10)
        }
    }

    private static func clamp01(_ v: Float) -> Float { Swift.min(Swift.max(v, 0), 1) }
}
