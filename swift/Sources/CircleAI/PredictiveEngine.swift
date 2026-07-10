// PredictiveEngine.swift
//
// Port of CircleAI.Companion predictive-engine layer — the C# reference:
//   - IPredictiveEngine + AnticipatedNeed   (HerJarvisContracts.cs)
//   - HistogramPredictiveEngine             (HerJarvisRealImplementations.cs)
//   - SequencePredictiveEngine              (SequencePredictiveEngine.cs)
//
// Contract #14: anticipate the user's upcoming needs.
// HistogramPredictiveEngine keeps a 24×7 time-of-day histogram per need and
// forecasts the ones whose recurring slots fall inside the horizon.
// SequencePredictiveEngine is a variable-order (n-gram) Markov chain over the
// user's event timeline with mean-inter-arrival forecasting and back-off.
//
// In-memory + deterministic.

import Foundation

// MARK: - AnticipatedNeed

/// A need the engine expects to arise, with the UTC time it is expected by and
/// a probability in [0, 1].
public struct AnticipatedNeed: Sendable, Equatable {
    public let description: String
    public let expectedByUtc: Date
    public let probability: Double

    public init(description: String, expectedByUtc: Date, probability: Double) {
        self.description = description
        self.expectedByUtc = expectedByUtc
        self.probability = probability
    }
}

// MARK: - IPredictiveEngine

/// Contract #14 — predictive engine.
public protocol IPredictiveEngine: AnyObject {
    /// Anticipate needs likely to arise within `horizonMinutes`.
    func anticipate(horizonMinutes: Int) async throws -> [AnticipatedNeed]
}

// MARK: - UTC day-of-week / hour helper

/// Computes the C# `(int)DayOfWeek * 24 + UtcDateTime.Hour` histogram slot for a
/// point in time. .NET `DayOfWeek` is Sunday=0 … Saturday=6; the hour is taken
/// from the UTC calendar. Range: 0 … 167.
enum TimeHistogramSlot {
    private static let utcCalendar: Calendar = {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        return cal
    }()

    static func of(_ date: Date) -> Int {
        let comps = utcCalendar.dateComponents([.weekday, .hour], from: date)
        // Swift weekday: 1=Sunday … 7=Saturday → .NET DayOfWeek 0…6.
        let dayOfWeek = (comps.weekday ?? 1) - 1
        let hour = comps.hour ?? 0
        return dayOfWeek * 24 + hour
    }
}

// MARK: - HistogramPredictiveEngine

/// Time-of-day histogram of recurring events. Each need owns a 24×7 slot array
/// (day-of-week × hour, UTC). `anticipate` walks the horizon in 30-minute steps,
/// sums the matching slots, and reports `upcoming / total` as the probability.
/// Ported from `HistogramPredictiveEngine` (HerJarvisRealImplementations.cs).
/// Descriptions are keyed case-insensitively.
public final class HistogramPredictiveEngine: IPredictiveEngine, @unchecked Sendable {
    private let lock = NSLock()
    // lowercased description -> (displayName, 168-slot histogram)
    private var hist: [String: (display: String, slots: [Int64])] = [:]

    public init() {}

    /// Tell the engine: this need occurred at this UTC time.
    public func observe(description: String, atUtc: Date) {
        precondition(!description.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "description required")
        let slot = TimeHistogramSlot.of(atUtc)
        lock.lock(); defer { lock.unlock() }
        if var existing = hist[description.lowercased()] {
            existing.slots[slot] += 1
            hist[description.lowercased()] = existing
        } else {
            var slots = [Int64](repeating: 0, count: 24 * 7)
            slots[slot] += 1
            hist[description.lowercased()] = (description, slots)
        }
    }

    public func anticipate(horizonMinutes: Int) async throws -> [AnticipatedNeed] {
        precondition(horizonMinutes > 0, "horizonMinutes out of range")
        let now = Date()
        lock.lock()
        let snapshot = hist
        lock.unlock()

        var results: [AnticipatedNeed] = []
        for (_, entry) in snapshot {
            let total = entry.slots.reduce(Int64(0), +)
            var upcoming: Int64 = 0
            var m = 0
            while m <= horizonMinutes {
                let when = now.addingTimeInterval(Double(m) * 60)
                let slot = TimeHistogramSlot.of(when)
                upcoming += entry.slots[slot]
                m += 30
            }
            if total == 0 || upcoming == 0 { continue }
            results.append(AnticipatedNeed(
                description: entry.display,
                expectedByUtc: now.addingTimeInterval(Double(horizonMinutes / 2) * 60),
                probability: Double(upcoming) / Double(total)))
        }
        // OrderByDescending(Probability).
        return results.sorted { $0.probability > $1.probability }
    }
}

// MARK: - SequencePredictiveEngine

/// Variable-order (n-gram) Markov chain over the user's event timeline. Predicts
/// next-likely events by backing off from the longest context to the shortest —
/// weighting a length-k context by 2^k — and forecasts each arrival from the
/// event's mean inter-arrival interval. Ported from `SequencePredictiveEngine`
/// (SequencePredictiveEngine.cs). Event names are compared case-sensitively
/// (Ordinal), matching the reference.
public final class SequencePredictiveEngine: IPredictiveEngine, @unchecked Sendable {
    private let lock = NSLock()
    // (previous-n-events joined by "|") -> { next event -> count }
    private var transitions: [String: [String: Int64]] = [:]
    // event -> (count, sumSeconds) for mean inter-arrival.
    private var interArrivals: [String: (count: Int64, sumSeconds: Double)] = [:]
    private var history: [(event: String, atUtc: Date)] = []
    private let order: Int

    public init(order: Int = 3) {
        precondition(order >= 1 && order <= 6, "order out of range")
        self.order = order
    }

    /// Add one event to the user timeline.
    public func observe(event: String, atUtc: Date) {
        precondition(!event.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "event required")
        lock.lock(); defer { lock.unlock() }
        history.append((event, atUtc))
        // Build n-gram contexts up to `order`.
        var k = 1
        while k <= order && history.count > k {
            let contextStart = history.count - 1 - k
            if contextStart < 0 { break }
            let contextItems = history[contextStart..<(contextStart + k)].map { $0.event }
            let key = contextItems.joined(separator: "|")
            var bucket = transitions[key] ?? [:]
            bucket[event, default: 0] += 1
            transitions[key] = bucket
            k += 1
        }
        // Track inter-arrival time for this event.
        if history.count >= 2 {
            let last = history[history.count - 2]
            if last.event == event {
                let gap = atUtc.timeIntervalSince(last.atUtc)
                if let prev = interArrivals[event] {
                    interArrivals[event] = (prev.count + 1, prev.sumSeconds + gap)
                } else {
                    interArrivals[event] = (1, gap)
                }
            }
        }
    }

    public func anticipate(horizonMinutes: Int) async throws -> [AnticipatedNeed] {
        precondition(horizonMinutes > 0, "horizonMinutes out of range")

        lock.lock()
        let snapshot = history
        let transitionsCopy = transitions
        let interArrivalsCopy = interArrivals
        lock.unlock()

        if snapshot.isEmpty { return [] }

        // Take the most recent `order` events as the prediction context.
        let contextLen = min(order, snapshot.count)
        let context = snapshot[(snapshot.count - contextLen)..<snapshot.count].map { $0.event }

        var totalScore: [String: Double] = [:]
        // Walk down from longest context to shortest (back-off), weighting
        // longer contexts higher.
        var k = context.count
        while k >= 1 {
            let key = context[(context.count - k)..<context.count].joined(separator: "|")
            k -= 1
            guard let bucket = transitionsCopy[key] else { continue }
            let totalForCtx = bucket.values.reduce(Int64(0), +)
            if totalForCtx == 0 { continue }
            let weight = pow(2.0, Double(k + 1))
            for (next, count) in bucket {
                let prob = Double(count) / Double(totalForCtx)
                totalScore[next, default: 0] += weight * prob
            }
        }

        if totalScore.isEmpty { return [] }

        let totalWeight = totalScore.values.reduce(0.0, +)
        let horizonSec = Double(horizonMinutes) * 60.0
        let now = Date()
        var anticipated: [AnticipatedNeed] = []
        // OrderByDescending(Value).
        for (ev, raw) in totalScore.sorted(by: { $0.value > $1.value }) {
            let prob = raw / totalWeight
            if prob <= 0 { continue }
            // Use the event's mean inter-arrival to estimate when it'll happen.
            let ia = interArrivalsCopy[ev]
            let meanInterval = (ia?.count ?? 0) > 0 ? ia!.sumSeconds / Double(ia!.count) : horizonSec * 0.5
            if meanInterval > horizonSec { continue } // not expected within window
            anticipated.append(AnticipatedNeed(
                description: ev,
                expectedByUtc: now.addingTimeInterval(meanInterval),
                probability: prob))
        }
        return anticipated
    }
}
