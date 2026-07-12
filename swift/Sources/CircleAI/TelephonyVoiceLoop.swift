// TelephonyVoiceLoop.swift
//
// Port of the pure-logic voice-loop layer from CircleAI.Telephony that sits
// between the carrier session (Telephony.swift) and the LLM. These types have
// no network / native dependencies — they are latency-shaving and turn-taking
// state machines exercised entirely in memory:
//   • BargeInController.cs        — BargeInState, BargeInTransition, BargeInOptions,
//                                   BargeInController.
//   • IvrLoopDetector.cs          — IvrRound, IvrLoopVerdict, IvrLoopDetector.
//   • SentenceChunker.cs          — SentenceChunker.
//   • LatencyTracker.cs           — LatencyStage, LatencySnapshot, LatencyTracker.
//   • FalseInterruptionTracker.cs — InterruptionStats, IFalseInterruptionTracker,
//                                   InMemoryFalseInterruptionTracker.
//   • SpeculativeGenerator.cs     — SpeculativeBranch, ResponseGenerator,
//                                   ISpeculativeGenerator, DefaultSpeculativeGenerator.
//   • ReassuranceFiller.cs        — ReassuranceVocabulary, ReassuranceFillerOptions,
//                                   IReassuranceFiller, DefaultReassuranceFiller.
//   • PromptVariableResolver.cs   — PromptVariableProvider, PromptVariableResolver.
//   • FirstMessagePreamble.cs     — FirstMessagePreambleOptions, IFirstMessagePreamble,
//                                   DefaultFirstMessagePreamble.
//
// CONVENTIONS:
//   • `sealed class` with mutable shared state → `final class @unchecked Sendable`
//     guarded by an `NSLock` (the C# `lock (_gate)` / `Interlocked` discipline).
//   • `sealed record` → `struct Sendable Equatable`.
//   • `Func<DateTimeOffset>` clock → `@Sendable () -> Date` (injected; defaults to
//     `Date.init`).
//   • `TimeSpan` → `TimeInterval` (seconds). C# `TimeSpan.FromMilliseconds(100)` →
//     `0.1`.
//   • `Interlocked.Add/Increment/Read/Exchange` on `long` → the same NSLock guard
//     (Swift has no portable atomics in this module).
//   • C# events / `Task.Delay`-driven fillers use Swift structured concurrency
//     (`Task`, `Task.sleep`, `withTaskCancellationHandler`).

import Foundation

// =====================================================================
// BargeInController.cs
// =====================================================================

/// State of the AI's current turn. Port of `CircleAI.Telephony.BargeInState`.
///
/// C# ordinals in declaration order: Speaking = 0, Paused = 1, Cancelled = 2,
/// Resumed = 3.
public enum BargeInState: Int, Sendable, Codable, CaseIterable {
    /// AI is speaking.
    case speaking = 0
    /// Caller interrupted; playback paused while we decide.
    case paused = 1
    /// Confirmed real interruption — turn cancelled.
    case cancelled = 2
    /// Decided false alarm — resumed speaking.
    case resumed = 3
}

/// One state transition. Port of the C# record
/// `CircleAI.Telephony.BargeInTransition`.
public struct BargeInTransition: Sendable, Equatable {
    public let from: BargeInState
    public let to: BargeInState
    public let at: Date
    public let reason: String

    public init(from: BargeInState, to: BargeInState, at: Date, reason: String) {
        self.from = from
        self.to = to
        self.at = at
        self.reason = reason
    }
}

/// Configuration for barge-in detection. Port of the C# record
/// `CircleAI.Telephony.BargeInOptions`.
///
/// `pauseAfter`: how long the caller must be talking before we pause. Default
/// 100 ms. `cancelAfter`: continued speech that confirms a real interruption.
/// Default 600 ms.
public struct BargeInOptions: Sendable, Equatable {
    public var pauseAfter: TimeInterval?
    public var cancelAfter: TimeInterval?

    public init(pauseAfter: TimeInterval? = nil, cancelAfter: TimeInterval? = nil) {
        self.pauseAfter = pauseAfter
        self.cancelAfter = cancelAfter
    }

    public var pauseAfterOrDefault: TimeInterval { pauseAfter ?? 0.100 }
    public var cancelAfterOrDefault: TimeInterval { cancelAfter ?? 0.600 }
}

/// Drives barge-in pause/resume/cancel decisions. Port of
/// `CircleAI.Telephony.BargeInController`.
public final class BargeInController: @unchecked Sendable {
    private let options: BargeInOptions
    private let clock: @Sendable () -> Date
    private let gate = NSLock()
    private var _state: BargeInState = .speaking
    private var callerSpeechStartedAt: Date?

    public init(options: BargeInOptions? = nil, clock: (@Sendable () -> Date)? = nil) {
        self.options = options ?? BargeInOptions()
        self.clock = clock ?? { Date() }
    }

    /// The current state of the AI turn.
    public var state: BargeInState {
        gate.lock(); defer { gate.unlock() }
        return _state
    }

    /// Call when AI playback begins.
    public func onPlaybackStart() {
        gate.lock(); defer { gate.unlock() }
        _state = .speaking
        callerSpeechStartedAt = nil
    }

    /// Call on each frame where the VAD reports caller speech.
    public func onCallerSpeech() -> BargeInTransition? {
        let now = clock()
        gate.lock(); defer { gate.unlock() }

        if _state == .cancelled { return nil }

        guard let started = callerSpeechStartedAt else {
            callerSpeechStartedAt = now
            return nil
        }

        let elapsed = now.timeIntervalSince(started)
        if _state == .speaking && elapsed >= options.pauseAfterOrDefault {
            let t = BargeInTransition(
                from: _state, to: .paused, at: now,
                reason: "Caller speech \(Self.ms(elapsed)) ms")
            _state = .paused
            return t
        }
        if _state == .paused && elapsed >= options.cancelAfterOrDefault {
            let t = BargeInTransition(
                from: _state, to: .cancelled, at: now,
                reason: "Confirmed barge-in after \(Self.ms(elapsed)) ms")
            _state = .cancelled
            return t
        }
        return nil
    }

    /// Call on each frame where VAD reports silence.
    public func onCallerSilence() -> BargeInTransition? {
        let now = clock()
        gate.lock(); defer { gate.unlock() }

        callerSpeechStartedAt = nil

        if _state == .paused {
            let t = BargeInTransition(
                from: _state, to: .resumed, at: now,
                reason: "Caller fell silent after pause")
            _state = .speaking // resume
            return t
        }
        return nil
    }

    /// Whether the AI should keep emitting audio frames right now.
    public var shouldEmitAudio: Bool {
        gate.lock(); defer { gate.unlock() }
        return _state == .speaking
    }

    /// Whether the turn was confirmed barge-in (caller wins, AI should drop).
    public var wasBargedIn: Bool {
        gate.lock(); defer { gate.unlock() }
        return _state == .cancelled
    }

    /// Mirror of C#'s `$"{elapsed.TotalMilliseconds:F0}"` — whole milliseconds.
    private static func ms(_ seconds: TimeInterval) -> String {
        String(format: "%.0f", seconds * 1000.0)
    }
}

// =====================================================================
// IvrLoopDetector.cs
// =====================================================================

/// One observation in the IVR conversation. Port of the C# record
/// `CircleAI.Telephony.IvrRound`.
public struct IvrRound: Sendable, Equatable {
    /// Text heard from the IVR.
    public let speech: String
    /// Digits the AI sent in response, if any.
    public let dtmfPressed: String?
    /// When this round happened.
    public let at: Date

    public init(speech: String, dtmfPressed: String?, at: Date) {
        self.speech = speech
        self.dtmfPressed = dtmfPressed
        self.at = at
    }
}

/// Verdict on IVR navigation health. Port of the C# record
/// `CircleAI.Telephony.IvrLoopVerdict`.
public struct IvrLoopVerdict: Sendable, Equatable {
    /// True if the navigator looks stuck.
    public let isLooping: Bool
    /// Estimated length of the repeating cycle (number of rounds).
    public let loopLength: Int
    /// Human-readable reason.
    public let reason: String

    public init(isLooping: Bool, loopLength: Int, reason: String) {
        self.isLooping = isLooping
        self.loopLength = loopLength
        self.reason = reason
    }
}

/// Records IVR rounds and surfaces a loop verdict. Port of
/// `CircleAI.Telephony.IvrLoopDetector`.
public final class IvrLoopDetector: @unchecked Sendable {
    private var rounds: [IvrRound] = []
    private let maxRoundsToTrack: Int
    private let minRoundsForLoop: Int
    private let similarityThreshold: Double
    private let gate = NSLock()

    public init(
        maxRoundsToTrack: Int = 32,
        minRoundsForLoop: Int = 2,
        similarityThreshold: Double = 0.85
    ) {
        self.maxRoundsToTrack = maxRoundsToTrack
        self.minRoundsForLoop = minRoundsForLoop
        self.similarityThreshold = similarityThreshold
    }

    /// Append one round and return the current verdict.
    public func observe(_ round: IvrRound) -> IvrLoopVerdict {
        gate.lock(); defer { gate.unlock() }
        rounds.append(round)
        while rounds.count > maxRoundsToTrack {
            rounds.removeFirst()
        }
        return evaluate()
    }

    /// Current verdict without adding a new round.
    public func currentVerdict() -> IvrLoopVerdict {
        gate.lock(); defer { gate.unlock() }
        return evaluate()
    }

    /// Drop all history.
    public func reset() {
        gate.lock(); defer { gate.unlock() }
        rounds.removeAll()
    }

    private func evaluate() -> IvrLoopVerdict {
        // Strong signal first — same DTMF + similar prompt three times in a row.
        if rounds.count >= 3 {
            let tail = Array(rounds.suffix(3))
            if tail.allSatisfy({ $0.dtmfPressed == tail[0].dtmfPressed }) &&
                tail.allSatisfy({ similarTo($0.speech, tail[0].speech) }) {
                return IvrLoopVerdict(isLooping: true, loopLength: 1,
                    reason: "Same prompt-and-press triple in a row.")
            }
        }

        if rounds.count < minRoundsForLoop * 2 {
            return IvrLoopVerdict(isLooping: false, loopLength: 0,
                reason: "Not enough rounds to evaluate.")
        }

        // Look for a repeating cycle of length L in the last N rounds.
        var length = minRoundsForLoop
        while length <= rounds.count / 2 {
            let tail = Array(rounds.suffix(2 * length))
            var looped = true
            for i in 0..<length {
                if !similarTo(tail[i].speech, tail[length + i].speech) ||
                    tail[i].dtmfPressed != tail[length + i].dtmfPressed {
                    looped = false
                    break
                }
            }
            if looped {
                return IvrLoopVerdict(isLooping: true, loopLength: length,
                    reason: "Detected repeating cycle of length \(length).")
            }
            length += 1
        }
        return IvrLoopVerdict(isLooping: false, loopLength: 0, reason: "No loop detected.")
    }

    private func similarTo(_ a: String, _ b: String) -> Bool {
        // C#: string.Equals(a, b, OrdinalIgnoreCase). a and b are non-optional
        // here (records carry non-null Speech), so the null guard is a no-op but
        // preserved for fidelity.
        if a.caseInsensitiveCompare(b) == .orderedSame { return true }
        // Cheap Jaccard over word sets, case-insensitive.
        let setA = Self.wordSet(a)
        let setB = Self.wordSet(b)
        if setA.isEmpty || setB.isEmpty { return false }
        let inter = setA.intersection(setB).count
        let union = setA.union(setB).count
        return Double(inter) / Double(union) >= similarityThreshold
    }

    /// Split on spaces, drop empties, lower-case for ordinal-ignore-case set
    /// membership (mirrors `StringComparer.OrdinalIgnoreCase` on the HashSet).
    private static func wordSet(_ s: String) -> Set<String> {
        Set(s.split(separator: " ", omittingEmptySubsequences: true).map { $0.lowercased() })
    }
}

// =====================================================================
// SentenceChunker.cs
// =====================================================================

/// Streaming sentence chunker. Accepts streamed LLM tokens and emits whole
/// sentences as soon as they're complete so TTS can start speaking before the
/// full response finishes. Port of `CircleAI.Telephony.SentenceChunker`.
public final class SentenceChunker: @unchecked Sendable {
    private static let terminalPunctuation: Set<Character> = [".", "!", "?", "。", "！", "？"]
    private var buffer = ""
    private let gate = NSLock()
    private let minSentenceLength: Int

    /// `minSentenceLength`: sentences below this character count are buffered
    /// with the next one (avoids "1." / "Mr." splits).
    public init(minSentenceLength: Int = 4) {
        self.minSentenceLength = minSentenceLength
    }

    /// Push a token; receive any complete sentences ready to emit.
    public func pushToken(_ token: String) -> [String] {
        if token.isEmpty { return [] }
        var ready: [String] = []
        gate.lock()
        buffer += token
        while true {
            let (chunk, kept) = extractNext(buffer)
            guard let chunk else { break }
            buffer = kept
            ready.append(chunk)
        }
        gate.unlock()
        return ready
    }

    /// Flush whatever's buffered as a final fragment, regardless of punctuation.
    public func flush() -> String {
        gate.lock(); defer { gate.unlock() }
        let s = buffer
        buffer = ""
        return s
    }

    /// Returns (chunk, kept). `chunk == nil` means no complete sentence yet.
    /// Mirrors the C# character-index scan over the buffer.
    private func extractNext(_ buffer: String) -> (String?, String) {
        let chars = Array(buffer)
        var searchFrom = 0
        while searchFrom < chars.count {
            // Find next terminal-punctuation index at or after searchFrom.
            guard let idx = Self.indexOfAny(chars, from: searchFrom) else {
                return (nil, buffer)
            }

            // Consume any trailing whitespace + closing quotes after the punctuation.
            var end = idx + 1
            while end < chars.count &&
                (chars[end].isWhitespace || chars[end] == "\"" || chars[end] == "'" || chars[end] == ")") {
                end += 1
            }

            let candidate = String(chars[0..<end]).trimmingCharacters(in: .whitespacesAndNewlines)
            if candidate.count >= minSentenceLength {
                return (candidate, String(chars[end...]))
            }
            // Too short — keep extending past this punctuation.
            searchFrom = end
        }
        return (nil, buffer)
    }

    private static func indexOfAny(_ chars: [Character], from: Int) -> Int? {
        var i = from
        while i < chars.count {
            if terminalPunctuation.contains(chars[i]) { return i }
            i += 1
        }
        return nil
    }
}

// =====================================================================
// LatencyTracker.cs
// =====================================================================

/// Stage names we track latency on. Port of the C# static class
/// `CircleAI.Telephony.LatencyStage`.
public enum LatencyStage {
    public static let asrFirstWord = "asr.first_word"
    public static let asrFinal = "asr.final"
    public static let llmFirstToken = "llm.first_token"
    public static let llmFullResponse = "llm.full_response"
    public static let ttsFirstAudio = "tts.first_audio"
    public static let ttsFullAudio = "tts.full_audio"
    public static let endToEnd = "voice_loop.end_to_end"
}

/// Snapshot of latency for one stage. Port of the C# record
/// `CircleAI.Telephony.LatencySnapshot`. `TimeSpan` fields become
/// `TimeInterval` seconds (source values are whole-millisecond samples).
public struct LatencySnapshot: Sendable, Equatable {
    public let stage: String
    public let samples: Int
    public let min: TimeInterval
    public let p50: TimeInterval
    public let p95: TimeInterval
    public let p99: TimeInterval
    public let max: TimeInterval

    public init(
        stage: String, samples: Int,
        min: TimeInterval, p50: TimeInterval, p95: TimeInterval, p99: TimeInterval, max: TimeInterval
    ) {
        self.stage = stage
        self.samples = samples
        self.min = min
        self.p50 = p50
        self.p95 = p95
        self.p99 = p99
        self.max = max
    }
}

/// Records latency observations and produces percentiles over a fixed-size
/// sliding window per stage. Port of `CircleAI.Telephony.LatencyTracker`.
///
/// The C# `ConcurrentDictionary<string, Queue<long>>` with per-queue `lock`s is
/// modelled with one `NSLock` guarding a `[String: [Int]]` (ms samples). The
/// FIFO window is a plain array trimmed from the front.
public final class LatencyTracker: @unchecked Sendable {
    private let windowSize: Int
    private let gate = NSLock()
    private var observations: [String: [Int]] = [:]

    public init(windowSize: Int = 256) {
        // C# throws ArgumentOutOfRangeException on <= 0; clamp defensively to 1
        // so the type never divides by an empty window (matches "positive size").
        precondition(windowSize > 0, "windowSize must be positive")
        self.windowSize = windowSize
    }

    /// Record one observation. Negative latencies are ignored (as in C#).
    public func record(_ stage: String, _ latency: TimeInterval) {
        if stage.isBlank { return }
        if latency < 0 { return }
        let ms = Int(latency * 1000.0)
        gate.lock()
        var q = observations[stage] ?? []
        q.append(ms)
        while q.count > windowSize { q.removeFirst() }
        observations[stage] = q
        gate.unlock()
    }

    /// Snapshot percentiles for one stage, or nil if unseen / empty.
    public func snapshot(_ stage: String) -> LatencySnapshot? {
        gate.lock()
        guard let q = observations[stage], !q.isEmpty else { gate.unlock(); return nil }
        var sorted = q
        gate.unlock()
        sorted.sort()

        func percentile(_ p: Double) -> TimeInterval {
            if sorted.isEmpty { return 0 }
            var idx = Int(ceil(p * Double(sorted.count))) - 1
            if idx < 0 { idx = 0 }
            if idx >= sorted.count { idx = sorted.count - 1 }
            return TimeInterval(sorted[idx]) / 1000.0
        }

        return LatencySnapshot(
            stage: stage,
            samples: sorted.count,
            min: TimeInterval(sorted[0]) / 1000.0,
            p50: percentile(0.50),
            p95: percentile(0.95),
            p99: percentile(0.99),
            max: TimeInterval(sorted[sorted.count - 1]) / 1000.0)
    }

    /// Snapshot every tracked stage (skips empties).
    public func snapshotAll() -> [LatencySnapshot] {
        gate.lock()
        let stages = Array(observations.keys)
        gate.unlock()
        var list: [LatencySnapshot] = []
        for stage in stages {
            if let snap = snapshot(stage) { list.append(snap) }
        }
        return list
    }

    public func reset(_ stage: String) {
        gate.lock(); defer { gate.unlock() }
        if observations[stage] != nil { observations[stage] = [] }
    }

    public func resetAll() {
        gate.lock(); defer { gate.unlock() }
        observations.removeAll()
    }
}

// =====================================================================
// FalseInterruptionTracker.cs
// =====================================================================

/// Counters for false-interruption monitoring. Port of the C# record
/// `CircleAI.Telephony.InterruptionStats`.
public struct InterruptionStats: Sendable, Equatable {
    public let totalPauseEvents: Int64
    public let confirmedBargeIns: Int64
    public let falseAlarms: Int64
    public let falseAlarmRate: Float

    public init(totalPauseEvents: Int64, confirmedBargeIns: Int64, falseAlarms: Int64, falseAlarmRate: Float) {
        self.totalPauseEvents = totalPauseEvents
        self.confirmedBargeIns = confirmedBargeIns
        self.falseAlarms = falseAlarms
        self.falseAlarmRate = falseAlarmRate
    }
}

/// Tracks barge-in transitions and surfaces a false-alarm rate. Port of
/// `CircleAI.Telephony.IFalseInterruptionTracker`.
public protocol IFalseInterruptionTracker: Sendable {
    /// Record one transition emitted by `BargeInController`.
    func record(_ transition: BargeInTransition)
    /// Current cumulative stats.
    func getStats() -> InterruptionStats
    /// Reset all counters.
    func reset()
}

/// Default in-memory tracker. Thread-safe. Port of
/// `CircleAI.Telephony.InMemoryFalseInterruptionTracker`.
///
/// The three `Interlocked`-managed `long` counters become NSLock-guarded
/// `Int64`s (paused→total, cancelled→confirmed, resumed→falseAlarms).
public final class InMemoryFalseInterruptionTracker: IFalseInterruptionTracker, @unchecked Sendable {
    private let gate = NSLock()
    private var totalPauses: Int64 = 0
    private var confirmed: Int64 = 0
    private var falseAlarms: Int64 = 0

    public init() {}

    public func record(_ transition: BargeInTransition) {
        gate.lock(); defer { gate.unlock() }
        switch transition.to {
        case .paused: totalPauses += 1
        case .cancelled: confirmed += 1
        case .resumed: falseAlarms += 1
        case .speaking: break
        }
    }

    public func getStats() -> InterruptionStats {
        gate.lock(); defer { gate.unlock() }
        let rate: Float = totalPauses > 0 ? Float(falseAlarms) / Float(totalPauses) : 0
        return InterruptionStats(
            totalPauseEvents: totalPauses,
            confirmedBargeIns: confirmed,
            falseAlarms: falseAlarms,
            falseAlarmRate: rate)
    }

    public func reset() {
        gate.lock(); defer { gate.unlock() }
        totalPauses = 0
        confirmed = 0
        falseAlarms = 0
    }
}

// =====================================================================
// SpeculativeGenerator.cs
// =====================================================================

/// Function that drives a response generation given a partial transcript. Port
/// of the C# delegate `CircleAI.Telephony.ResponseGenerator`
/// (`Task<string>(string transcript, CancellationToken)`). Cancellation is
/// carried by a Swift `Task`; the generator observes it via `Task.isCancelled`
/// / cooperative cancellation.
public typealias ResponseGenerator = @Sendable (_ transcript: String) async throws -> String

/// One in-flight speculative branch. Port of the C# record
/// `CircleAI.Telephony.SpeculativeBranch`. The C# `Task<string>` is held as the
/// backing `Task`; `PartialTranscript` + `StartedAt` are exposed for inspection.
public struct SpeculativeBranch: @unchecked Sendable {
    public let partialTranscript: String
    public let responseTask: Task<String, Error>
    public let startedAt: Date

    public init(partialTranscript: String, responseTask: Task<String, Error>, startedAt: Date) {
        self.partialTranscript = partialTranscript
        self.responseTask = responseTask
        self.startedAt = startedAt
    }
}

/// Manages speculative-generation branches. Port of
/// `CircleAI.Telephony.ISpeculativeGenerator`.
public protocol ISpeculativeGenerator: Sendable {
    /// The branch currently considered most likely to commit.
    var activeBranch: SpeculativeBranch? { get }
    /// Start (or restart) the speculative branch using `partialTranscript`.
    func speculate(_ partialTranscript: String, generator: @escaping ResponseGenerator)
    /// Commit to a final transcript and return the matching response.
    func commit(_ finalTranscript: String, generator: @escaping ResponseGenerator) async throws -> String
    /// Abort any active speculation.
    func abort()
}

/// Default driver. Cancels older branches when the partial diverges. Port of
/// `CircleAI.Telephony.DefaultSpeculativeGenerator`.
///
/// The C# `CancellationTokenSource` is modelled by cancelling the backing
/// `Task`. When a new partial is not an extension of the active one, the active
/// task is cancelled and a fresh one is launched.
public final class DefaultSpeculativeGenerator: ISpeculativeGenerator, @unchecked Sendable {
    private let gate = NSLock()
    private var active: SpeculativeBranch?
    private let clock: @Sendable () -> Date
    private let minPartialLength: Int

    public init(clock: (@Sendable () -> Date)? = nil, minPartialLength: Int = 8) {
        self.clock = clock ?? { Date() }
        self.minPartialLength = minPartialLength
    }

    public var activeBranch: SpeculativeBranch? {
        gate.lock(); defer { gate.unlock() }
        return active
    }

    public func speculate(_ partialTranscript: String, generator: @escaping ResponseGenerator) {
        if partialTranscript.isBlank { return }
        if partialTranscript.count < minPartialLength { return }

        var toCancel: Task<String, Error>?
        gate.lock()
        // If the new partial is just an extension of the active one, keep it.
        if let a = active,
           partialTranscript.lowercased().hasPrefix(a.partialTranscript.lowercased()) {
            gate.unlock()
            return
        }
        toCancel = active?.responseTask
        let task = Task { try await generator(partialTranscript) }
        active = SpeculativeBranch(partialTranscript: partialTranscript, responseTask: task, startedAt: clock())
        gate.unlock()
        toCancel?.cancel()
    }

    public func commit(_ finalTranscript: String, generator: @escaping ResponseGenerator) async throws -> String {
        if finalTranscript.isBlank { return "" }

        gate.lock()
        let active = self.active
        gate.unlock()

        if let active,
           finalTranscript.lowercased().hasPrefix(active.partialTranscript.lowercased()) {
            do {
                let draft = try await active.responseTask.value
                if finalTranscript.caseInsensitiveCompare(active.partialTranscript) == .orderedSame {
                    return draft
                }
                // Final extended the partial — finalize via a fresh generation
                // (our contract: re-run with the full transcript).
            } catch is CancellationError {
                // superseded — fall through
            } catch {
                // swallow draft errors — fall through
            }
        }

        // No usable speculative draft — generate fresh.
        var toCancel: Task<String, Error>?
        gate.lock()
        toCancel = self.active?.responseTask
        self.active = nil
        gate.unlock()
        toCancel?.cancel()

        return try await generator(finalTranscript)
    }

    public func abort() {
        var toCancel: Task<String, Error>?
        gate.lock()
        toCancel = active?.responseTask
        active = nil
        gate.unlock()
        toCancel?.cancel()
    }
}

// =====================================================================
// ReassuranceFiller.cs
// =====================================================================

/// Phrases the filler picks from, rotated to avoid repetition. Port of the C#
/// record `CircleAI.Telephony.ReassuranceVocabulary`.
public struct ReassuranceVocabulary: Sendable, Equatable {
    public let shortFillers: [String]
    public let longFillers: [String]

    public init(shortFillers: [String], longFillers: [String]) {
        self.shortFillers = shortFillers
        self.longFillers = longFillers
    }

    /// Sensible English defaults.
    public static let `default` = ReassuranceVocabulary(
        shortFillers: [
            "One moment.",
            "Let me check.",
            "Give me a sec.",
            "Just a moment.",
        ],
        longFillers: [
            "Still looking that up for you.",
            "This is taking a bit longer than usual — bear with me.",
            "Almost there — still pulling that information.",
            "Thanks for your patience, I'm checking that now.",
        ])
}

/// Configuration for the filler driver. Port of the C# record
/// `CircleAI.Telephony.ReassuranceFillerOptions`.
///
/// `shortFillerAfter`: silence after which to play a short filler. Default
/// 600 ms. `longFillerEvery`: cadence for long fillers after the first short
/// one. Default 3 s.
public struct ReassuranceFillerOptions: Sendable {
    public var shortFillerAfter: TimeInterval?
    public var longFillerEvery: TimeInterval?
    public var vocabulary: ReassuranceVocabulary?

    public init(
        shortFillerAfter: TimeInterval? = nil,
        longFillerEvery: TimeInterval? = nil,
        vocabulary: ReassuranceVocabulary? = nil
    ) {
        self.shortFillerAfter = shortFillerAfter
        self.longFillerEvery = longFillerEvery
        self.vocabulary = vocabulary
    }

    public var shortFillerAfterOrDefault: TimeInterval { shortFillerAfter ?? 0.600 }
    public var longFillerEveryOrDefault: TimeInterval { longFillerEvery ?? 3.0 }
    public var vocabularyOrDefault: ReassuranceVocabulary { vocabulary ?? .default }
}

/// Driver that plays fillers while a long task runs. Port of
/// `CircleAI.Telephony.IReassuranceFiller`.
///
/// The C# generic `RunWithFillerAsync<T>` is expressed with a Swift generic
/// `T: Sendable`. `work` is the awaited unit of work; while it is pending the
/// driver speaks fillers via `tts` into `session`.
public protocol IReassuranceFiller: Sendable {
    func runWithFiller<T: Sendable>(
        work: @escaping @Sendable () async throws -> T,
        session: ICallSession,
        tts: @escaping BriefingSynthesiser
    ) async throws -> T
}

/// Default in-memory filler driver. Port of
/// `CircleAI.Telephony.DefaultReassuranceFiller`.
public final class DefaultReassuranceFiller: IReassuranceFiller, @unchecked Sendable {
    private let options: ReassuranceFillerOptions
    private let gate = NSLock()
    private var shortRotation: Int = 0
    private var longRotation: Int = 0

    public init(options: ReassuranceFillerOptions? = nil) {
        self.options = options ?? ReassuranceFillerOptions()
    }

    public func runWithFiller<T: Sendable>(
        work: @escaping @Sendable () async throws -> T,
        session: ICallSession,
        tts: @escaping BriefingSynthesiser
    ) async throws -> T {
        // Launch the filler loop concurrently; cancel it when work finishes
        // (mirrors the linked CancellationTokenSource the C# cancels on both the
        // success and the throwing path).
        let fillerTask = Task { [weak self] in
            await self?.speakFillers(session: session, tts: tts)
        }
        do {
            let result = try await work()
            fillerTask.cancel()
            _ = await fillerTask.value
            return result
        } catch {
            fillerTask.cancel()
            _ = await fillerTask.value
            throw error
        }
    }

    private func speakFillers(session: ICallSession, tts: @escaping BriefingSynthesiser) async {
        let vocab = options.vocabularyOrDefault
        do {
            try await Task.sleep(nanoseconds: Self.nanos(options.shortFillerAfterOrDefault))
            try await speak(session: session, tts: tts, text: nextShort(vocab))

            while !Task.isCancelled {
                try await Task.sleep(nanoseconds: Self.nanos(options.longFillerEveryOrDefault))
                try await speak(session: session, tts: tts, text: nextLong(vocab))
            }
        } catch {
            // Task.sleep throws CancellationError when work finishes — expected.
        }
    }

    private func nextShort(_ v: ReassuranceVocabulary) -> String {
        if v.shortFillers.isEmpty { return "One moment." }
        gate.lock()
        let idx = shortRotation
        shortRotation += 1
        gate.unlock()
        return v.shortFillers[abs(idx) % v.shortFillers.count]
    }

    private func nextLong(_ v: ReassuranceVocabulary) -> String {
        if v.longFillers.isEmpty { return "Almost there." }
        gate.lock()
        let idx = longRotation
        longRotation += 1
        gate.unlock()
        return v.longFillers[abs(idx) % v.longFillers.count]
    }

    private func speak(session: ICallSession, tts: @escaping BriefingSynthesiser, text: String) async throws {
        let audio = try await tts(text)
        if !audio.isEmpty {
            try await session.sendAudio(AudioFrame(pcm: audio, format: .pcm24000, offset: 0))
        }
    }

    /// C# `Interlocked.Increment(ref r) - 1` yields 0,1,2,… on first calls. The
    /// NSLock post-increment above reproduces that (read then +1). `abs` guards
    /// the wrap the C# `Math.Abs` guards.
    private static func nanos(_ seconds: TimeInterval) -> UInt64 {
        UInt64((seconds * 1_000_000_000).rounded())
    }
}

// =====================================================================
// PromptVariableResolver.cs
// =====================================================================

/// Resolves the value for one prompt variable. Port of the C# delegate
/// `CircleAI.Telephony.PromptVariableProvider`
/// (`ValueTask<string?>(string variableName, CancellationToken)`).
public typealias PromptVariableProvider = @Sendable (_ variableName: String) async throws -> String?

/// Render a template with `{{var}}` placeholders against a set of providers.
/// Port of `CircleAI.Telephony.PromptVariableResolver`.
///
/// C# uses `Dictionary<..>(StringComparer.OrdinalIgnoreCase)`; here the keys are
/// stored lower-cased and looked up lower-cased to preserve case-insensitive
/// resolution while the substitution reads the *matched* name back out of the
/// template.
public final class PromptVariableResolver: @unchecked Sendable {
    // `{{ name }}` with an identifier body: [A-Za-z_][A-Za-z0-9_.]*
    private static let variablePattern = try! NSRegularExpression(
        pattern: #"\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}"#)

    private let gate = NSLock()
    private var providers: [String: PromptVariableProvider] = [:]
    private var statics: [String: String] = [:]
    private let defaultMissing: String

    public init(defaultMissing: String = "") {
        self.defaultMissing = defaultMissing
    }

    /// Register a static value. Returns self for chaining (mirrors C#).
    @discardableResult
    public func set(_ name: String, _ value: String) -> PromptVariableResolver {
        precondition(!name.isBlank, "name required")
        gate.lock(); statics[name.lowercased()] = value; gate.unlock()
        return self
    }

    /// Register a dynamic value provider (e.g. CRM lookup).
    @discardableResult
    public func setProvider(_ name: String, _ provider: @escaping PromptVariableProvider) -> PromptVariableResolver {
        precondition(!name.isBlank, "name required")
        gate.lock(); providers[name.lowercased()] = provider; gate.unlock()
        return self
    }

    /// Render `template` by substituting every `{{var}}`.
    public func render(_ template: String) async throws -> String {
        if template.isEmpty { return "" }

        let ns = template as NSString
        let matches = Self.variablePattern.matches(in: template, range: NSRange(location: 0, length: ns.length))
        if matches.isEmpty { return template }

        // Resolve each distinct variable once (case-insensitive), preserving the
        // first-seen ordering the C# loop implies.
        var replacements: [String: String] = [:]
        for m in matches {
            let name = ns.substring(with: m.range(at: 1))
            let key = name.lowercased()
            if replacements[key] != nil { continue }

            gate.lock()
            let staticValue = statics[key]
            let provider = providers[key]
            gate.unlock()

            if let staticValue {
                replacements[key] = staticValue
                continue
            }
            if let provider {
                let resolved = try await provider(name)
                replacements[key] = resolved ?? defaultMissing
                continue
            }
            replacements[key] = defaultMissing
        }

        // Replace back-to-front so earlier NSRange offsets stay valid.
        var result = template
        for m in matches.reversed() {
            let name = ns.substring(with: m.range(at: 1))
            let value = replacements[name.lowercased()] ?? defaultMissing
            if let r = Range(m.range, in: result) {
                result.replaceSubrange(r, with: value)
            }
        }
        return result
    }
}

// =====================================================================
// FirstMessagePreamble.cs
// =====================================================================

/// Configuration for the first-message preamble. Port of the C# record
/// `CircleAI.Telephony.FirstMessagePreambleOptions`.
///
/// `maxLatency`: if the LLM responds before this elapses, skip the preamble.
/// Default 250 ms.
public struct FirstMessagePreambleOptions: Sendable, Equatable {
    public let template: String
    public var maxLatency: TimeInterval?

    public init(template: String, maxLatency: TimeInterval? = nil) {
        self.template = template
        self.maxLatency = maxLatency
    }

    public var maxLatencyOrDefault: TimeInterval { maxLatency ?? 0.250 }
}

/// Speaks a greeting at call-start. Port of
/// `CircleAI.Telephony.IFirstMessagePreamble`.
///
/// `modelReady` is the C# `Task` awaited concurrently: if it completes
/// successfully before `maxLatency`, the preamble is skipped. It is modelled as
/// a Swift async closure the driver races against a timeout.
public protocol IFirstMessagePreamble: Sendable {
    func speak(
        session: ICallSession,
        tts: @escaping BriefingSynthesiser,
        modelReady: @escaping @Sendable () async -> Void
    ) async throws
}

/// Default driver that resolves the template via a `PromptVariableResolver`.
/// Port of `CircleAI.Telephony.DefaultFirstMessagePreamble`.
public final class DefaultFirstMessagePreamble: IFirstMessagePreamble, @unchecked Sendable {
    private let options: FirstMessagePreambleOptions
    private let resolver: PromptVariableResolver

    public init(options: FirstMessagePreambleOptions, resolver: PromptVariableResolver? = nil) {
        self.options = options
        self.resolver = resolver ?? PromptVariableResolver()
    }

    public func speak(
        session: ICallSession,
        tts: @escaping BriefingSynthesiser,
        modelReady: @escaping @Sendable () async -> Void
    ) async throws {
        // Race the model against the latency window. If the model wins, skip the
        // preamble. `Task.select`-style: whichever finishes first cancels the
        // other. `modelWon` records that the model completed within the window.
        let maxLatency = options.maxLatencyOrDefault
        let modelWon = await withTaskGroup(of: Bool.self, returning: Bool.self) { group in
            group.addTask {
                await modelReady()
                return true   // model completed
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64((maxLatency * 1_000_000_000).rounded()))
                return false  // race window elapsed
            }
            // First result decides; cancel the loser.
            let first = await group.next() ?? false
            group.cancelAll()
            return first
        }
        if modelWon { return }

        let rendered = try await resolver.render(options.template)
        if rendered.isBlank { return }

        let audio = try await tts(rendered)
        if audio.isEmpty { return }

        try await session.sendAudio(AudioFrame(pcm: audio, format: .pcm24000, offset: 0))
    }
}
