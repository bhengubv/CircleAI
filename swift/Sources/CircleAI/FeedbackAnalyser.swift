// FeedbackAnalyser.swift
//
// Analyses a window of FeedbackSignal records and produces PersonaAdaptation
// deltas. Ported from CircleAI.Memory.FeedbackAnalyser (C#), mirroring the
// verified TypeScript port (memory/feedback_analyser.ts).
//
// Rules (applied to the most-recent N signals, default N = 20):
//   - >70% negative signals → verbosityDelta = -0.1
//   - >70% positive signals → verbosityDelta = +0.05
//   - formalityDelta is always 0 (reserved for future heuristics)
//   - preferredTopics is always empty — FeedbackSignal carries no topic tags
//
// PersonaAdaptation holds `Float` deltas at exactly the points C# uses `float`
// literals (-0.1f, +0.05f); Swift `Float` is 32-bit native so the cross-language
// fixture contract stays byte-identical.

import Foundation

// MARK: - PersonaAdaptation

/// Deltas to apply to `PersonaState` after analysing feedback signals.
public struct PersonaAdaptation: Sendable, Equatable {
    /// Change to apply to persona verbosity.
    public let verbosityDelta: Float
    /// Change to apply to persona formality (always 0 — reserved).
    public let formalityDelta: Float
    /// Topics inferred from feedback (always empty — FeedbackSignal has no tags).
    public let preferredTopics: [String]

    public init(verbosityDelta: Float, formalityDelta: Float, preferredTopics: [String]) {
        self.verbosityDelta = verbosityDelta
        self.formalityDelta = formalityDelta
        self.preferredTopics = preferredTopics
    }
}

// MARK: - FeedbackAnalyser

/// Analyses recent `FeedbackSignal` records and produces `PersonaAdaptation`
/// adjustments.
public struct FeedbackAnalyser: Sendable {

    /// FP32 delta constants, matching the C# `float` literals exactly.
    private static let verbosityDown: Float = -0.1
    private static let verbosityUp: Float = 0.05

    private let windowSize: Int

    /// - Parameter windowSize: Number of most-recent signals to consider.
    ///   Must be at least 1. Default 20.
    public init(windowSize: Int = 20) {
        precondition(windowSize >= 1, "Window size must be at least 1.")
        self.windowSize = windowSize
    }

    /// Compute persona adaptation from the provided signals.
    ///
    /// `verbosityDelta` is:
    ///   - -0.1  when more than 70% of the window is negative
    ///   - +0.05 when more than 70% of the window is positive
    ///   - 0     otherwise
    ///
    /// `formalityDelta` is always 0 and `preferredTopics` is always empty
    /// because `FeedbackSignal` carries no topic metadata.
    public func analyse(_ signals: [FeedbackSignal]) -> PersonaAdaptation {
        // Newest-first, then take the window.
        let window = signals
            .sorted { $0.recordedAt > $1.recordedAt }
            .prefix(windowSize)

        if window.isEmpty {
            return PersonaAdaptation(verbosityDelta: 0, formalityDelta: 0, preferredTopics: [])
        }

        let positiveCount = window.filter { $0.polarity == .positive }.count
        let negativeCount = window.filter { $0.polarity == .negative }.count
        let total = window.count

        var verbosityDelta: Float = 0
        let negativeRatio = Float(negativeCount) / Float(total)
        let positiveRatio = Float(positiveCount) / Float(total)

        if negativeRatio > 0.70 {
            verbosityDelta = FeedbackAnalyser.verbosityDown
        } else if positiveRatio > 0.70 {
            verbosityDelta = FeedbackAnalyser.verbosityUp
        }

        // FeedbackSignal has no tags — topic extraction is deferred.
        return PersonaAdaptation(verbosityDelta: verbosityDelta, formalityDelta: 0, preferredTopics: [])
    }
}
