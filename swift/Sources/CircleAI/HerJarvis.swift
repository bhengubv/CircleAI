// HerJarvis.swift
//
// Port of the HER/Jarvis-level companion contracts + their real, in-process
// implementations. C# reference:
//   - HerJarvisContracts.cs            (the interfaces + supporting records)
//   - HerJarvisRealImplementations.cs  (the ConcurrentDictionary / Channel /
//                                       simple-math backings)
//
// This file ports the contracts assigned to this work unit:
//     IAlwaysOnPresence         → HeartbeatAlwaysOnPresence
//     IFusedPerception          → ChannelFusedPerception
//     IContinuousLearner        → EwaContinuousLearner
//     IGoalPursuer              → InMemoryGoalPursuer
//     IVoiceIdentity            → EnergyBandVoiceIdentity
//     ICalibratedConfidence     → HistoricalCalibratedConfidence
//     IEmotionSensor            → KeywordEmotionSensor
//     ISkillAcquisition         → DemoStoreSkillAcquisition
//     IBioSignalStream          → ChannelBioSignalStream
//     IPhysicalActuator         → RegistryPhysicalActuator
//     IAgentPeerNetwork         → MailboxAgentPeerNetwork
//     IFederatedFineTuner       → InMemoryFederatedFineTuner
//     IFirstTokenOptimizer      → SlidingP50FirstTokenOptimizer
//     ICryptoDelegation         → EcdsaCryptoDelegation
//     ICodeGenerationLoop       → SyntaxCheckingCodeGenerationLoop
//     ISelfImprovementLoop      → TrackingSelfImprovementLoop
//
// The theory-of-mind (#10), world-model (#5), inner-monologue (#13), and
// predictive-engine (#14) contracts are ported in TheoryOfMind.swift,
// WorldModel.swift, InnerMonologue.swift, and PredictiveEngine.swift.
//
// Everything here is in-memory + deterministic. Where the C# binds a native
// crypto primitive we use CryptoKit (P-256 ECDSA). Streaming pub/sub uses
// AsyncStream where C# uses System.Threading.Channels.

import Foundation
import CryptoKit

// =====================================================================
// 1. Always-on background presence across all devices.
// =====================================================================

/// Always-on background presence. `start` / `stop` toggle a heartbeat; hosts
/// keep the companion process resident.
public protocol IAlwaysOnPresence: AnyObject {
    var isRunning: Bool { get }
    func start() async throws
    func stop() async throws
}

/// Timer-driven heartbeat with start/stop. Ported from
/// `HeartbeatAlwaysOnPresence`. The heartbeat count is observable so tests can
/// assert the timer actually ticked.
public final class HeartbeatAlwaysOnPresence: IAlwaysOnPresence, @unchecked Sendable {
    private let lock = NSLock()
    private let heartbeatInterval: TimeInterval
    private var timer: DispatchSourceTimer?
    private var ticks: Int64 = 0

    public init(heartbeatInterval: TimeInterval = 30) {
        self.heartbeatInterval = heartbeatInterval
    }

    public var isRunning: Bool {
        lock.lock(); defer { lock.unlock() }
        return timer != nil
    }

    public var heartbeats: Int64 {
        lock.lock(); defer { lock.unlock() }
        return ticks
    }

    public func start() async throws {
        lock.lock(); defer { lock.unlock() }
        if timer != nil { return }
        let t = DispatchSource.makeTimerSource(queue: DispatchQueue.global())
        // Fire immediately (matching C#'s TimeSpan.Zero due time), then repeat.
        t.schedule(deadline: .now(), repeating: heartbeatInterval)
        t.setEventHandler { [weak self] in
            guard let self else { return }
            self.lock.lock(); self.ticks += 1; self.lock.unlock()
        }
        timer = t
        t.resume()
    }

    public func stop() async throws {
        lock.lock(); defer { lock.unlock() }
        timer?.cancel()
        timer = nil
    }
}

// =====================================================================
// 2. Fused perceptual stream.
// =====================================================================

/// One fused percept — a synchronised snapshot across modalities at a moment.
public struct FusedPercept: Sendable, Equatable {
    public let at: Date
    public let vision: String?
    public let audio: String?
    public let text: String?
    public let sensors: [String: Double]

    public init(at: Date, vision: String? = nil, audio: String? = nil,
                text: String? = nil, sensors: [String: Double] = [:]) {
        self.at = at
        self.vision = vision
        self.audio = audio
        self.text = text
        self.sensors = sensors
    }
}

/// A fused perceptual stream — vision + audio + text + sensors, time-aligned.
public protocol IFusedPerception: AnyObject {
    func stream() -> AsyncStream<FusedPercept>
}

/// Channel-based pub/sub with a `publish` hook. Ported from
/// `ChannelFusedPerception`. Multiple `stream()` subscribers each get their own
/// AsyncStream; published percepts fan out to all live subscribers.
public final class ChannelFusedPerception: IFusedPerception, @unchecked Sendable {
    private let lock = NSLock()
    private var continuations: [UUID: AsyncStream<FusedPercept>.Continuation] = [:]
    private var completed = false

    public init() {}

    public func publish(_ p: FusedPercept) {
        lock.lock(); defer { lock.unlock() }
        guard !completed else { return }
        for cont in continuations.values { cont.yield(p) }
    }

    public func complete() {
        // Snapshot + clear under the lock, then finish() OUTSIDE it: finish() can
        // synchronously invoke each continuation's onTermination, which re-acquires
        // this same non-reentrant NSLock → self-deadlock if the lock is still held.
        lock.lock()
        completed = true
        let conts = Array(continuations.values)
        continuations.removeAll()
        lock.unlock()
        for cont in conts { cont.finish() }
    }

    public func stream() -> AsyncStream<FusedPercept> {
        AsyncStream { continuation in
            let id = UUID()
            lock.lock()
            if completed {
                lock.unlock()
                continuation.finish()
                return
            }
            continuations[id] = continuation
            lock.unlock()
            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.continuations[id] = nil; self.lock.unlock()
            }
        }
    }
}

// =====================================================================
// 4. Continuous online learning.
// =====================================================================

/// Continuous online learning — feed back a scalar reward per interaction and
/// the learner adjusts its running estimate.
public protocol IContinuousLearner: AnyObject {
    func registerFeedback(interactionId: String, reward: Double, contextJson: String) async throws
}

/// Exponentially-weighted average reward per interaction id. Ported from
/// `EwaContinuousLearner`. First observation seeds the average with the raw
/// reward; subsequent observations blend by `alpha`.
public final class EwaContinuousLearner: IContinuousLearner, @unchecked Sendable {
    private let lock = NSLock()
    private var state: [String: (avg: Double, weight: Double)] = [:]
    private let alpha: Double

    public init(alpha: Double = 0.2) {
        precondition(alpha > 0 && alpha <= 1, "alpha out of range")
        self.alpha = alpha
    }

    public func registerFeedback(interactionId: String, reward: Double, contextJson: String) async throws {
        precondition(!interactionId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "interactionId required")
        lock.lock(); defer { lock.unlock() }
        if let prev = state[interactionId] {
            state[interactionId] = (prev.avg * (1 - alpha) + reward * alpha, prev.weight + 1)
        } else {
            state[interactionId] = (reward, 1.0)
        }
    }

    public func averageRewardOf(_ interactionId: String) -> Double? {
        lock.lock(); defer { lock.unlock() }
        return state[interactionId]?.avg
    }

    public func observationsOf(_ interactionId: String) -> Int64 {
        lock.lock(); defer { lock.unlock() }
        return Int64(state[interactionId]?.weight ?? 0)
    }
}

// =====================================================================
// 6. Multi-month goal pursuit with replanning.
// =====================================================================

/// A long-horizon goal: id, description, deadline, a JSON plan of milestones,
/// and a progress fraction in [0, 1].
public struct LongHorizonGoal: Sendable, Equatable {
    public let id: String
    public let description: String
    public let deadlineUtc: Date
    public let planJson: String
    public let progressFraction: Double

    public init(id: String, description: String, deadlineUtc: Date,
                planJson: String, progressFraction: Double) {
        self.id = id
        self.description = description
        self.deadlineUtc = deadlineUtc
        self.planJson = planJson
        self.progressFraction = progressFraction
    }

    /// Value-style "with" — mirrors C# `record with { … }`.
    func with(planJson: String? = nil, progressFraction: Double? = nil) -> LongHorizonGoal {
        LongHorizonGoal(
            id: id,
            description: description,
            deadlineUtc: deadlineUtc,
            planJson: planJson ?? self.planJson,
            progressFraction: progressFraction ?? self.progressFraction)
    }
}

/// Multi-month goal pursuit with replanning.
public protocol IGoalPursuer: AnyObject {
    func register(description: String, deadlineUtc: Date) async throws -> LongHorizonGoal
    func current(id: String) async throws -> LongHorizonGoal?
    func replan(id: String) async throws
}

/// Stores a goal + a computed milestone plan; `replan` recomputes the plan from
/// "now" to the deadline. Ported from `InMemoryGoalPursuer`. The milestone plan
/// spaces `min(8, max(2, totalDays/14))` evenly-spaced milestones from now to
/// the deadline and serialises them as a JSON object matching the reference's
/// hand-built string (ISO-8601 "O"-format due dates).
public final class InMemoryGoalPursuer: IGoalPursuer, @unchecked Sendable {
    private let lock = NSLock()
    private var goals: [String: LongHorizonGoal] = [:]

    public init() {}

    public func register(description: String, deadlineUtc: Date) async throws -> LongHorizonGoal {
        precondition(!description.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "description required")
        let id = Self.newId()
        let now = Date()
        if deadlineUtc <= now {
            throw HerJarvisError.invalidArgument("deadline must be in the future")
        }
        let plan = Self.buildPlan(description: description, now: now, deadlineUtc: deadlineUtc)
        let g = LongHorizonGoal(id: id, description: description, deadlineUtc: deadlineUtc,
                                planJson: plan, progressFraction: 0)
        lock.lock(); goals[id] = g; lock.unlock()
        return g
    }

    public func current(id: String) async throws -> LongHorizonGoal? {
        lock.lock(); defer { lock.unlock() }
        return goals[id]
    }

    public func replan(id: String) async throws {
        lock.lock(); defer { lock.unlock() }
        guard let g = goals[id] else {
            throw HerJarvisError.invalidOperation("Unknown goal \(id)")
        }
        let plan = Self.buildPlan(description: g.description, now: Date(), deadlineUtc: g.deadlineUtc)
        goals[id] = g.with(planJson: plan)
    }

    /// Set the progress fraction of a goal. Mirrors the C# `Progress` helper.
    public func progress(id: String, fraction: Double) throws {
        precondition(fraction >= 0 && fraction <= 1, "fraction out of range")
        lock.lock(); defer { lock.unlock() }
        guard let g = goals[id] else {
            throw HerJarvisError.invalidOperation("Unknown goal \(id)")
        }
        goals[id] = g.with(progressFraction: fraction)
    }

    static func newId() -> String { UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() }

    static func buildPlan(description: String, now: Date, deadlineUtc: Date) -> String {
        let totalDays = max(1, Int((deadlineUtc.timeIntervalSince(now)) / 86400.0))
        let milestones = min(8, max(2, totalDays / 14))
        let stepSeconds = deadlineUtc.timeIntervalSince(now) / Double(milestones)
        var sb = "{\"description\":" + NetJson.string(description) + ",\"milestones\":["
        for i in 1...milestones {
            if i > 1 { sb += "," }
            let due = now.addingTimeInterval(stepSeconds * Double(i))
            sb += "{\"index\":\(i),\"due\":\"" + NetJson.iso8601Round(due) + "\"}"
        }
        sb += "]}"
        return sb
    }
}

// =====================================================================
// 8. Per-user voice continuity (MFCC fingerprint).
// =====================================================================

/// Per-user voice continuity — identify a returning speaker and enrol new ones
/// from raw PCM-16 audio.
public protocol IVoiceIdentity: AnyObject {
    /// Returns a stable voice fingerprint id (or nil if unknown).
    func identify(audioPcm16: Data, sampleRateHz: Int) async throws -> String?
    func enroll(userId: String, audioPcm16: Data, sampleRateHz: Int) async throws
}

/// MFCC (mean-cepstral) fingerprint over windowed audio + cosine-similarity
/// nearest-neighbour matching. Ported byte-for-byte from `EnergyBandVoiceIdentity`:
/// pre-emphasis → 25 ms/10 ms framing → Hamming window → direct-DFT power
/// spectrum → 26 mel filters → log → DCT-II → mean of the first 13 coefficients.
/// A candidate matches when cosine similarity > 0.85.
public final class EnergyBandVoiceIdentity: IVoiceIdentity, @unchecked Sendable {
    private let lock = NSLock()
    private var enrolled: [String: [[Double]]] = [:]

    private static let numCoefficients = 13
    private static let numMelFilters = 26
    private static let frameSize = 400   // 25 ms @ 16 kHz
    private static let frameStep = 160   // 10 ms @ 16 kHz
    private static let preEmphasis: Float = 0.97

    public init() {}

    public func enroll(userId: String, audioPcm16: Data, sampleRateHz: Int) async throws {
        precondition(!userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "userId required")
        let fp = Self.mfcc(audioPcm16, sampleRateHz: sampleRateHz)
        lock.lock(); defer { lock.unlock() }
        enrolled[userId, default: []].append(fp)
    }

    public func identify(audioPcm16: Data, sampleRateHz: Int) async throws -> String? {
        let fp = Self.mfcc(audioPcm16, sampleRateHz: sampleRateHz)
        var best: String? = nil
        var bestSim = -1.0
        lock.lock()
        for (user, references) in enrolled {
            for reference in references {
                let sim = Self.cosineSimilarity(fp, reference)
                if sim > bestSim { bestSim = sim; best = user }
            }
        }
        lock.unlock()
        return bestSim > 0.85 ? best : nil
    }

    /// Compute mean MFCC vector across all frames.
    static func mfcc(_ pcm16: Data, sampleRateHz: Int) -> [Double] {
        var samples = decodePcm16(pcm16)
        if samples.count < frameSize { return [Double](repeating: 0, count: numCoefficients) }
        preEmphasisFilter(&samples)
        let filters = melFilterbank(numFilters: numMelFilters, frameSize: frameSize, sampleRateHz: sampleRateHz)

        var sum = [Double](repeating: 0, count: numCoefficients)
        var count = 0
        let window = hammingWindow(frameSize)
        var start = 0
        while start + frameSize <= samples.count {
            var frame = [Float](repeating: 0, count: frameSize)
            for i in 0..<frameSize { frame[i] = samples[start + i] * window[i] }
            let powerSpec = powerSpectrum(frame)
            let melEnergies = applyFilterbank(powerSpec, filters)
            var logEnergies = [Double](repeating: 0, count: numMelFilters)
            for i in 0..<numMelFilters { logEnergies[i] = log(max(1e-10, melEnergies[i])) }
            let coeffs = dct(logEnergies, numCoeffs: numCoefficients)
            for i in 0..<numCoefficients { sum[i] += coeffs[i] }
            count += 1
            start += frameStep
        }
        if count == 0 { return sum }
        for i in 0..<numCoefficients { sum[i] /= Double(count) }
        return sum
    }

    static func decodePcm16(_ pcm16: Data) -> [Float] {
        let n = pcm16.count / 2
        var samples = [Float](repeating: 0, count: n)
        pcm16.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            let bytes = raw.bindMemory(to: UInt8.self)
            for i in 0..<n {
                // little-endian signed 16-bit
                let lo = Int(bytes[i * 2])
                let hi = Int(Int8(bitPattern: bytes[i * 2 + 1]))
                let s = Int16(truncatingIfNeeded: (hi << 8) | lo)
                samples[i] = Float(s) / 32768.0
            }
        }
        return samples
    }

    static func preEmphasisFilter(_ samples: inout [Float]) {
        guard samples.count > 1 else { return }
        var i = samples.count - 1
        while i > 0 {
            samples[i] -= preEmphasis * samples[i - 1]
            i -= 1
        }
    }

    static func hammingWindow(_ n: Int) -> [Float] {
        var w = [Float](repeating: 0, count: n)
        for i in 0..<n {
            w[i] = 0.54 - 0.46 * Float(cos(2 * Double.pi * Double(i) / Double(n - 1)))
        }
        return w
    }

    /// Magnitude-squared spectrum via direct DFT (frame is small).
    static func powerSpectrum(_ frame: [Float]) -> [Double] {
        let n = frame.count
        let half = n / 2 + 1
        var spec = [Double](repeating: 0, count: half)
        for k in 0..<half {
            var re = 0.0, im = 0.0
            let omega = -2.0 * Double.pi * Double(k) / Double(n)
            for t in 0..<n {
                re += Double(frame[t]) * cos(omega * Double(t))
                im += Double(frame[t]) * sin(omega * Double(t))
            }
            spec[k] = re * re + im * im
        }
        return spec
    }

    /// Build mel-filterbank weights: `numFilters` triangular filters over the bins.
    static func melFilterbank(numFilters: Int, frameSize: Int, sampleRateHz: Int) -> [[Double]] {
        func hzToMel(_ hz: Double) -> Double { 2595 * log10(1 + hz / 700.0) }
        func melToHz(_ mel: Double) -> Double { 700 * (pow(10, mel / 2595) - 1) }
        let lowMel = hzToMel(0)
        let highMel = hzToMel(Double(sampleRateHz) / 2.0)
        let pointCount = numFilters + 2
        var melPoints = [Double](repeating: 0, count: pointCount)
        for i in 0..<pointCount {
            melPoints[i] = lowMel + (highMel - lowMel) * Double(i) / Double(pointCount - 1)
        }
        var binPoints = [Int](repeating: 0, count: pointCount)
        for i in 0..<pointCount {
            binPoints[i] = Int(floor(Double(frameSize + 1) * melToHz(melPoints[i]) / Double(sampleRateHz)))
        }

        let half = frameSize / 2 + 1
        var filters = [[Double]](repeating: [Double](repeating: 0, count: half), count: numFilters)
        for m in 0..<numFilters {
            let left = binPoints[m]
            let centre = binPoints[m + 1]
            let right = binPoints[m + 2]
            var k = left
            while k < centre && k < half {
                if centre != left { filters[m][k] = Double(k - left) / Double(centre - left) }
                k += 1
            }
            k = centre
            while k < right && k < half {
                if right != centre { filters[m][k] = Double(right - k) / Double(right - centre) }
                k += 1
            }
        }
        return filters
    }

    static func applyFilterbank(_ powerSpec: [Double], _ filters: [[Double]]) -> [Double] {
        var energies = [Double](repeating: 0, count: filters.count)
        for m in 0..<filters.count {
            var sum = 0.0
            let filter = filters[m]
            let len = min(powerSpec.count, filter.count)
            for k in 0..<len { sum += powerSpec[k] * filter[k] }
            energies[m] = sum
        }
        return energies
    }

    /// DCT-II keeping the first `numCoeffs` coefficients.
    static func dct(_ input: [Double], numCoeffs: Int) -> [Double] {
        let n = input.count
        var output = [Double](repeating: 0, count: numCoeffs)
        for k in 0..<numCoeffs {
            var sum = 0.0
            for i in 0..<n { sum += input[i] * cos(Double.pi * Double(k) * (Double(i) + 0.5) / Double(n)) }
            output[k] = sum
        }
        return output
    }

    static func cosineSimilarity(_ a: [Double], _ b: [Double]) -> Double {
        var dot = 0.0, na = 0.0, nb = 0.0
        let count = min(a.count, b.count)
        for i in 0..<count { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i] }
        return (na == 0 || nb == 0) ? 0 : dot / (sqrt(na) * sqrt(nb))
    }
}

// =====================================================================
// 9. Calibrated uncertainty at orchestration.
// =====================================================================

/// A calibrated confidence interval — lower and upper bounds in [0, 1].
public struct ConfidenceBand: Sendable, Equatable {
    public let lower: Double
    public let upper: Double
    public init(lower: Double, upper: Double) {
        self.lower = lower
        self.upper = upper
    }
}

/// Calibrated uncertainty at orchestration time.
public protocol ICalibratedConfidence: AnyObject {
    func evaluate(answer: String, contextJson: String) async throws -> ConfidenceBand
}

/// Platt-style calibration over an observed correctness history. Ported from
/// `HistoricalCalibratedConfidence`. Computes a raw score from answer length /
/// hedge words / context presence, then (once ≥ 5 outcomes are recorded)
/// calibrates to the empirical hit-rate of the 5 nearest raw scores, and returns
/// a band whose half-width shrinks as calibrated confidence rises.
public final class HistoricalCalibratedConfidence: ICalibratedConfidence, @unchecked Sendable {
    private let lock = NSLock()
    private var history: [(rawScore: Double, wasCorrect: Bool)] = []

    // \b(maybe|perhaps|might|possibly|unclear|don't know)\b  (case-insensitive)
    private static let hedgeRx: NSRegularExpression = {
        // swiftlint:disable:next force_try
        try! NSRegularExpression(
            pattern: #"\b(maybe|perhaps|might|possibly|unclear|don't know)\b"#,
            options: [.caseInsensitive])
    }()

    public init() {}

    public func recordOutcome(rawScore: Double, wasCorrect: Bool) {
        lock.lock(); defer { lock.unlock() }
        history.append((min(max(rawScore, 0), 1), wasCorrect))
    }

    public func evaluate(answer: String, contextJson: String) async throws -> ConfidenceBand {
        let raw = Self.computeRawScore(answer: answer, contextJson: contextJson)
        var calibrated: Double
        lock.lock()
        if history.count < 5 {
            calibrated = raw
        } else {
            let nearby = history.sorted { abs($0.rawScore - raw) < abs($1.rawScore - raw) }.prefix(5)
            let correct = nearby.filter { $0.wasCorrect }.count
            calibrated = Double(correct) / Double(nearby.count)
        }
        lock.unlock()
        let halfBand = max(0.05, 0.25 - calibrated * 0.2)
        return ConfidenceBand(
            lower: max(0, calibrated - halfBand),
            upper: min(1, calibrated + halfBand))
    }

    static func computeRawScore(answer: String, contextJson: String) -> Double {
        let trimmed = answer.trimmingCharacters(in: .whitespacesAndNewlines)
        let len = max(1, trimmed.count)
        let ns = answer as NSString
        let hedges = hedgeRx.numberOfMatches(in: answer, options: [], range: NSRange(location: 0, length: ns.length))
        let hedgePenalty = min(0.5, Double(hedges) * 0.1)
        let hasContext = !contextJson.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && contextJson.count > 2
        let v = (log(Double(len)) / 10.0) + (hasContext ? 0.1 : 0) - hedgePenalty
        return min(max(v, 0), 1)
    }
}

// =====================================================================
// 11. Emotion sensing.
// =====================================================================

/// One emotion reading — a discrete label plus arousal/valence coordinates.
public struct EmotionFrame: Sendable, Equatable {
    public let label: String
    public let arousal: Double
    public let valence: Double
    public init(label: String, arousal: Double, valence: Double) {
        self.label = label
        self.arousal = arousal
        self.valence = valence
    }
}

/// Emotion sensing from a fused-signal JSON blob.
public protocol IEmotionSensor: AnyObject {
    func sense(fusedJson: String) async throws -> EmotionFrame
}

/// Keyword + arousal/valence inference from fused JSON. Ported from
/// `KeywordEmotionSensor`. Each of six emotion patterns contributes its
/// (arousal, valence) weighted by its match count; the dominant label is the
/// most-matched pattern. No matches → neutral (0, 0).
public final class KeywordEmotionSensor: IEmotionSensor, @unchecked Sendable {
    private struct Pattern {
        let label: String
        let arousal: Double
        let valence: Double
        let rx: NSRegularExpression
    }

    private static func rx(_ p: String) -> NSRegularExpression {
        // swiftlint:disable:next force_try
        try! NSRegularExpression(pattern: p, options: [.caseInsensitive])
    }

    private static let patterns: [Pattern] = [
        Pattern(label: "joy", arousal: 0.8, valence: 0.9, rx: rx(#"\b(happy|joy|delight|excited|love|wonderful)\b"#)),
        Pattern(label: "anger", arousal: 0.9, valence: -0.8, rx: rx(#"\b(angry|furious|rage|hate|annoyed)\b"#)),
        Pattern(label: "sad", arousal: 0.3, valence: -0.7, rx: rx(#"\b(sad|lonely|grief|cry|depressed|down)\b"#)),
        Pattern(label: "fear", arousal: 0.85, valence: -0.6, rx: rx(#"\b(afraid|scared|terrified|anxious|worried)\b"#)),
        Pattern(label: "surprise", arousal: 0.7, valence: 0.3, rx: rx(#"\b(surprised|amazed|astonished|wow)\b"#)),
        Pattern(label: "calm", arousal: 0.1, valence: 0.5, rx: rx(#"\b(calm|peaceful|relaxed|content|fine)\b"#)),
    ]

    public init() {}

    public func sense(fusedJson: String) async throws -> EmotionFrame {
        let ns = fusedJson as NSString
        let range = NSRange(location: 0, length: ns.length)
        var hits: [(label: String, arousal: Double, valence: Double, count: Int)] = []
        for p in Self.patterns {
            let c = p.rx.numberOfMatches(in: fusedJson, options: [], range: range)
            if c > 0 { hits.append((p.label, p.arousal, p.valence, c)) }
        }
        if hits.isEmpty { return EmotionFrame(label: "neutral", arousal: 0.0, valence: 0.0) }
        let totalWeight = hits.reduce(0) { $0 + $1.count }
        let arousal = hits.reduce(0.0) { $0 + $1.arousal * Double($1.count) } / Double(totalWeight)
        let valence = hits.reduce(0.0) { $0 + $1.valence * Double($1.count) } / Double(totalWeight)
        // OrderByDescending(Count).First() — stable: first-declared pattern wins ties.
        let top = hits.max { a, b in a.count < b.count }!.label
        return EmotionFrame(label: top, arousal: arousal, valence: valence)
    }
}

// =====================================================================
// 12. Skill acquisition.
// =====================================================================

/// A learned skill: id, name, and the JSON demonstration it was acquired from.
public struct AcquiredSkill: Sendable, Equatable {
    public let id: String
    public let name: String
    public let descriptionJson: String
    public init(id: String, name: String, descriptionJson: String) {
        self.id = id
        self.name = name
        self.descriptionJson = descriptionJson
    }
}

/// Skill acquisition — learn a new skill from a demonstration; list what's known.
public protocol ISkillAcquisition: AnyObject {
    func acquire(demonstrationJson: String) async throws -> AcquiredSkill
    func list() async throws -> [AcquiredSkill]
}

/// Demonstration store with name extraction + alphabetical listing. Ported from
/// `DemoStoreSkillAcquisition`. If the demonstration JSON has a top-level
/// string `"name"`, that becomes the skill name; otherwise `skill-<id[..6]>`.
public final class DemoStoreSkillAcquisition: ISkillAcquisition, @unchecked Sendable {
    private let lock = NSLock()
    private var skills: [String: AcquiredSkill] = [:]

    public init() {}

    public func acquire(demonstrationJson: String) async throws -> AcquiredSkill {
        let id = InMemoryGoalPursuer.newId()
        let name = Self.extractName(demonstrationJson) ?? "skill-" + String(id.prefix(6))
        let skill = AcquiredSkill(id: id, name: name, descriptionJson: demonstrationJson)
        lock.lock(); skills[id] = skill; lock.unlock()
        return skill
    }

    public func list() async throws -> [AcquiredSkill] {
        lock.lock(); defer { lock.unlock() }
        // OrderBy(Name) — ordinal, matching StringComparer default for OrderBy.
        return skills.values.sorted { $0.name < $1.name }
    }

    static func extractName(_ demonstrationJson: String) -> String? {
        guard let data = demonstrationJson.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let name = obj["name"] as? String else { return nil }
        return name
    }
}

// =====================================================================
// 17. Bio-signal integration.
// =====================================================================

/// A single bio-signal sample — kind (e.g. "hr", "hrv"), value, and timestamp.
public struct BioSignal: Sendable, Equatable {
    public let kind: String
    public let value: Double
    public let at: Date
    public init(kind: String, value: Double, at: Date) {
        self.kind = kind
        self.value = value
        self.at = at
    }
}

/// A fan-in stream of bio-signals from wearables.
public protocol IBioSignalStream: AnyObject {
    func stream() -> AsyncStream<BioSignal>
}

/// Fan-in channel with a `publish` hook. Ported from `ChannelBioSignalStream`.
public final class ChannelBioSignalStream: IBioSignalStream, @unchecked Sendable {
    private let lock = NSLock()
    private var continuations: [UUID: AsyncStream<BioSignal>.Continuation] = [:]
    private var completed = false

    public init() {}

    public func publish(_ s: BioSignal) {
        lock.lock(); defer { lock.unlock() }
        guard !completed else { return }
        for cont in continuations.values { cont.yield(s) }
    }

    public func complete() {
        // Snapshot + clear under the lock, then finish() OUTSIDE it: finish() can
        // synchronously invoke each continuation's onTermination, which re-acquires
        // this same non-reentrant NSLock → self-deadlock if the lock is still held.
        lock.lock()
        completed = true
        let conts = Array(continuations.values)
        continuations.removeAll()
        lock.unlock()
        for cont in conts { cont.finish() }
    }

    public func stream() -> AsyncStream<BioSignal> {
        AsyncStream { continuation in
            let id = UUID()
            lock.lock()
            if completed { lock.unlock(); continuation.finish(); return }
            continuations[id] = continuation
            lock.unlock()
            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.continuations[id] = nil; self.lock.unlock()
            }
        }
    }
}

// =====================================================================
// 18. Robotics / physical actuation.
// =====================================================================

/// A command to a physical device — device id, action name, and string args.
public struct PhysicalCommand: Sendable, Equatable {
    public let deviceId: String
    public let action: String
    public let args: [String: String]
    public init(deviceId: String, action: String, args: [String: String] = [:]) {
        self.deviceId = deviceId
        self.action = action
        self.args = args
    }
}

/// The outcome of a physical command.
public struct PhysicalCommandResult: Sendable, Equatable {
    public let succeeded: Bool
    public let error: String?
    public init(succeeded: Bool, error: String? = nil) {
        self.succeeded = succeeded
        self.error = error
    }
}

/// Robotics / physical actuation — dispatch a command to a registered device.
public protocol IPhysicalActuator: AnyObject {
    func invoke(command: PhysicalCommand) async throws -> PhysicalCommandResult
}

/// Device-handler registry with per-device dispatch. Ported from
/// `RegistryPhysicalActuator`. Unknown devices fail with a descriptive error
/// rather than throwing — matching the reference.
public final class RegistryPhysicalActuator: IPhysicalActuator, @unchecked Sendable {
    public typealias Handler = @Sendable (PhysicalCommand) async -> PhysicalCommandResult

    private let lock = NSLock()
    private var handlers: [String: Handler] = [:]

    public init() {}

    public func registerDevice(_ deviceId: String, handler: @escaping Handler) {
        precondition(!deviceId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "deviceId required")
        lock.lock(); handlers[deviceId] = handler; lock.unlock()
    }

    public func invoke(command: PhysicalCommand) async throws -> PhysicalCommandResult {
        lock.lock()
        let h = handlers[command.deviceId]
        lock.unlock()
        guard let h else {
            return PhysicalCommandResult(succeeded: false, error: "Unknown device '\(command.deviceId)'")
        }
        return await h(command)
    }
}

// =====================================================================
// 19. Agent-to-agent peer protocol.
// =====================================================================

/// One message between two agents.
public struct AgentToAgentMessage: Sendable, Equatable {
    public let fromAgentId: String
    public let toAgentId: String
    public let payload: String
    public let at: Date
    public init(fromAgentId: String, toAgentId: String, payload: String, at: Date) {
        self.fromAgentId = fromAgentId
        self.toAgentId = toAgentId
        self.payload = payload
        self.at = at
    }
}

/// Agent-to-agent peer protocol — send to another agent's mailbox, receive yours.
public protocol IAgentPeerNetwork: AnyObject {
    func send(message: AgentToAgentMessage) async throws
    func receive(forAgentId: String) -> AsyncStream<AgentToAgentMessage>
}

/// In-memory mailbox per agent id. Ported from `MailboxAgentPeerNetwork`.
/// Messages sent before a receiver subscribes are buffered and delivered when
/// the receiver's stream begins (matching the Channel semantics of the C#
/// reference, where the unbounded channel retains writes until read).
public final class MailboxAgentPeerNetwork: IAgentPeerNetwork, @unchecked Sendable {
    private let lock = NSLock()
    private var buffered: [String: [AgentToAgentMessage]] = [:]
    private var continuations: [String: AsyncStream<AgentToAgentMessage>.Continuation] = [:]

    public init() {}

    public func send(message: AgentToAgentMessage) async throws {
        lock.lock(); defer { lock.unlock() }
        if let cont = continuations[message.toAgentId] {
            cont.yield(message)
        } else {
            buffered[message.toAgentId, default: []].append(message)
        }
    }

    public func receive(forAgentId: String) -> AsyncStream<AgentToAgentMessage> {
        precondition(!forAgentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "forAgentId required")
        return AsyncStream { continuation in
            lock.lock()
            // Flush anything buffered before this subscription.
            if let pending = buffered[forAgentId] {
                for m in pending { continuation.yield(m) }
                buffered[forAgentId] = nil
            }
            continuations[forAgentId] = continuation
            lock.unlock()
            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock()
                self.continuations[forAgentId] = nil
                self.lock.unlock()
            }
        }
    }
}

// =====================================================================
// 20. Federated / on-device fine-tune pipeline.
// =====================================================================

/// Status of a fine-tune job — id, progress [0, 1], and an optional error.
public struct FineTuneJobStatus: Sendable, Equatable {
    public let jobId: String
    public let progress: Double
    public let error: String?
    public init(jobId: String, progress: Double, error: String? = nil) {
        self.jobId = jobId
        self.progress = progress
        self.error = error
    }

    func with(progress: Double? = nil, error: String?? = nil) -> FineTuneJobStatus {
        FineTuneJobStatus(
            jobId: jobId,
            progress: progress ?? self.progress,
            error: error ?? self.error)
    }
}

/// Federated / on-device fine-tune pipeline — start a job, poll its status.
public protocol IFederatedFineTuner: AnyObject {
    func start(baseModel: String, trainingDataPath: String) async throws -> String
    func status(jobId: String) async throws -> FineTuneJobStatus
}

/// Job runner with progress tracking. Ported from `InMemoryFederatedFineTuner`.
/// A host may inject a trainer closure; the default trainer walks the training
/// file line-by-line reporting progress, then completes at 1.0. Unknown job ids
/// report `"unknown job"`.
public final class InMemoryFederatedFineTuner: IFederatedFineTuner, @unchecked Sendable {
    public typealias Trainer = @Sendable (_ baseModel: String, _ path: String,
                                          _ report: @escaping @Sendable (Double) -> Void) async -> Void

    private let lock = NSLock()
    private var jobs: [String: FineTuneJobStatus] = [:]
    private let trainer: Trainer

    public init(trainer: Trainer? = nil) {
        self.trainer = trainer ?? Self.defaultTrainer
    }

    public func start(baseModel: String, trainingDataPath: String) async throws -> String {
        precondition(!baseModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "baseModel required")
        precondition(!trainingDataPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "trainingDataPath required")
        let jobId = InMemoryGoalPursuer.newId()
        lock.lock(); jobs[jobId] = FineTuneJobStatus(jobId: jobId, progress: 0, error: nil); lock.unlock()

        let report: @Sendable (Double) -> Void = { [weak self] p in
            guard let self else { return }
            self.lock.lock()
            if let cur = self.jobs[jobId] { self.jobs[jobId] = cur.with(progress: min(max(p, 0), 1)) }
            self.lock.unlock()
        }

        Task { [trainer] in
            await trainer(baseModel, trainingDataPath, report)
            self.lock.lock()
            if let cur = self.jobs[jobId] { self.jobs[jobId] = cur.with(progress: 1.0, error: .some(nil)) }
            self.lock.unlock()
        }
        return jobId
    }

    public func status(jobId: String) async throws -> FineTuneJobStatus {
        lock.lock(); defer { lock.unlock() }
        return jobs[jobId] ?? FineTuneJobStatus(jobId: jobId, progress: 0, error: "unknown job")
    }

    static func defaultTrainer(_ baseModel: String, _ path: String,
                               _ report: @escaping @Sendable (Double) -> Void) async {
        let lineCount: Int
        if let content = try? String(contentsOfFile: path, encoding: .utf8) {
            lineCount = content.split(separator: "\n", omittingEmptySubsequences: false).count
        } else {
            lineCount = 100
        }
        let step = 1.0 / Double(max(1, lineCount))
        for i in 0..<lineCount {
            report(Double(i) * step)
            await Task.yield()
        }
        report(1.0)
    }
}

// =====================================================================
// 21. Sub-100 ms first-token latency tracking.
// =====================================================================

/// The first-token latency budget — the target and the current p50.
public struct FirstTokenBudget: Sendable, Equatable {
    public let targetMs: Int
    public let currentP50Ms: Int
    public init(targetMs: Int, currentP50Ms: Int) {
        self.targetMs = targetMs
        self.currentP50Ms = currentP50Ms
    }
}

/// Sub-100 ms first-token latency tracker.
public protocol IFirstTokenOptimizer: AnyObject {
    func current() async throws -> FirstTokenBudget
}

/// Sliding-window p50 latency tracker. Ported from
/// `SlidingP50FirstTokenOptimizer`. Keeps the last `windowSize` samples; the
/// reported p50 is `sorted[count/2]` (upper-median, matching the reference).
public final class SlidingP50FirstTokenOptimizer: IFirstTokenOptimizer, @unchecked Sendable {
    private let lock = NSLock()
    private var samples: [Int] = []
    private let windowSize: Int
    private let targetMs: Int

    public init(targetMs: Int = 100, windowSize: Int = 256) {
        precondition(targetMs > 0, "targetMs out of range")
        precondition(windowSize > 0, "windowSize out of range")
        self.targetMs = targetMs
        self.windowSize = windowSize
    }

    public func recordFirstTokenLatency(_ ms: Int) {
        precondition(ms >= 0, "ms out of range")
        lock.lock(); defer { lock.unlock() }
        samples.append(ms)
        while samples.count > windowSize { samples.removeFirst() }
    }

    public func current() async throws -> FirstTokenBudget {
        lock.lock()
        let p50: Int
        if samples.isEmpty {
            p50 = 0
        } else {
            let sorted = samples.sorted()
            p50 = sorted[sorted.count / 2]
        }
        lock.unlock()
        return FirstTokenBudget(targetMs: targetMs, currentP50Ms: p50)
    }
}

// =====================================================================
// 22. Cryptographic delegation framework (P-256 ECDSA).
// =====================================================================

/// A signed delegation credential — issuer, subject, scope, expiry, signature.
public struct DelegationCredential: Sendable, Equatable {
    public let issuer: String
    public let subjectId: String
    public let scope: String
    public let expiresAtUtc: Date
    public let signature: String
    public init(issuer: String, subjectId: String, scope: String,
                expiresAtUtc: Date, signature: String) {
        self.issuer = issuer
        self.subjectId = subjectId
        self.scope = scope
        self.expiresAtUtc = expiresAtUtc
        self.signature = signature
    }
}

/// Cryptographic delegation framework — issue + verify short-lived credentials.
public protocol ICryptoDelegation: AnyObject {
    func issue(subjectId: String, scope: String, lifetime: TimeInterval) throws -> DelegationCredential
    func verify(credential: DelegationCredential) -> Bool
}

/// P-256 ECDSA sign + verify over a canonical `issuer|subject|scope|expiry`
/// payload. Ported from `EcdsaCryptoDelegation` (C# uses `ECDsa` on
/// `nistP256`; here we use CryptoKit's `P256.Signing`). Signatures are base64
/// DER. Verification checks issuer match, expiry, and signature validity.
public final class EcdsaCryptoDelegation: ICryptoDelegation, @unchecked Sendable {
    private let key: P256.Signing.PrivateKey
    private let issuer: String

    public init(issuer: String = "circleai-companion", key: P256.Signing.PrivateKey? = nil) {
        precondition(!issuer.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "issuer required")
        self.issuer = issuer
        self.key = key ?? P256.Signing.PrivateKey()
    }

    public func issue(subjectId: String, scope: String, lifetime: TimeInterval) throws -> DelegationCredential {
        precondition(!subjectId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "subjectId required")
        precondition(!scope.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "scope required")
        precondition(lifetime > 0, "lifetime out of range")
        let expires = Date().addingTimeInterval(lifetime)
        let payload = canonical(subjectId: subjectId, scope: scope, expiresAtUtc: expires)
        let signature = try key.signature(for: Data(payload.utf8))
        return DelegationCredential(
            issuer: issuer, subjectId: subjectId, scope: scope, expiresAtUtc: expires,
            signature: signature.derRepresentation.base64EncodedString())
    }

    public func verify(credential: DelegationCredential) -> Bool {
        if credential.issuer != issuer { return false }
        if credential.expiresAtUtc <= Date() { return false }
        if credential.signature.isEmpty { return false }
        guard let sigData = Data(base64Encoded: credential.signature),
              let signature = try? P256.Signing.ECDSASignature(derRepresentation: sigData) else {
            return false
        }
        let payload = canonical(subjectId: credential.subjectId, scope: credential.scope,
                                expiresAtUtc: credential.expiresAtUtc)
        return key.publicKey.isValidSignature(signature, for: Data(payload.utf8))
    }

    private func canonical(subjectId: String, scope: String, expiresAtUtc: Date) -> String {
        "\(issuer)|\(subjectId)|\(scope)|\(NetJson.iso8601Round(expiresAtUtc))"
    }
}

// =====================================================================
// 23. Live code generation + test + deploy loop.
// =====================================================================

/// One code-gen job — prompt, generated snippet, whether tests pass, deploy hint.
public struct CodeGenJob: Sendable, Equatable {
    public let id: String
    public let prompt: String
    public let outputSnippet: String
    public let testsPass: Bool
    public let deployHint: String?
    public init(id: String, prompt: String, outputSnippet: String,
                testsPass: Bool, deployHint: String?) {
        self.id = id
        self.prompt = prompt
        self.outputSnippet = outputSnippet
        self.testsPass = testsPass
        self.deployHint = deployHint
    }
}

/// Live code generation + test + deploy loop.
public protocol ICodeGenerationLoop: AnyObject {
    func run(prompt: String) async throws -> CodeGenJob
}

/// Generates a snippet, checks brace/paren/bracket balance, runs registered
/// tests, and only then produces a deploy hint. Ported from
/// `SyntaxCheckingCodeGenerationLoop`. Generator / test-runner / deploy-hint
/// are injectable; defaults echo the prompt, gate on balance, and choose an
/// inline-vs-nuget hint.
public final class SyntaxCheckingCodeGenerationLoop: ICodeGenerationLoop, @unchecked Sendable {
    public typealias Generator = @Sendable (String) async -> String
    public typealias TestRunner = @Sendable (String) async -> Bool
    public typealias DeploymentHint = @Sendable (String) -> String?

    private let generator: Generator
    private let testRunner: TestRunner
    private let deploymentHint: DeploymentHint

    public init(generator: Generator? = nil,
                testRunner: TestRunner? = nil,
                deploymentHint: DeploymentHint? = nil) {
        self.generator = generator ?? Self.defaultGenerator
        self.testRunner = testRunner ?? Self.defaultTestRunner
        self.deploymentHint = deploymentHint ?? Self.defaultDeploymentHint
    }

    public func run(prompt: String) async throws -> CodeGenJob {
        precondition(!prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "prompt required")
        let id = InMemoryGoalPursuer.newId()
        let snippet = await generator(prompt)
        let parses = Self.isSyntacticallyBalanced(snippet)
        let runnerOk = await testRunner(snippet)
        let testsOk = parses && runnerOk
        return CodeGenJob(id: id, prompt: prompt, outputSnippet: snippet,
                          testsPass: testsOk, deployHint: testsOk ? deploymentHint(snippet) : nil)
    }

    static func defaultGenerator(_ prompt: String) async -> String {
        "// (3.3.0) generated from: \(prompt.replacingOccurrences(of: "\n", with: " "))\nreturn 0;"
    }

    static func defaultTestRunner(_ snippet: String) async -> Bool {
        isSyntacticallyBalanced(snippet)
    }

    static func defaultDeploymentHint(_ snippet: String) -> String? {
        snippet.contains("public class") ? "stage as nuget" : "run inline"
    }

    static func isSyntacticallyBalanced(_ snippet: String) -> Bool {
        if snippet.isEmpty { return false }
        var curly = 0, paren = 0, square = 0
        for c in snippet {
            switch c {
            case "{": curly += 1
            case "}": curly -= 1
            case "(": paren += 1
            case ")": paren -= 1
            case "[": square += 1
            case "]": square -= 1
            default: break
            }
            if curly < 0 || paren < 0 || square < 0 { return false }
        }
        return curly == 0 && paren == 0 && square == 0
    }
}

// =====================================================================
// 24. Self-debugging / self-improvement loop.
// =====================================================================

/// The verdict of one self-improvement cycle — what was applied, and the score.
public struct SelfImprovementVerdict: Sendable, Equatable {
    public let improvementsApplied: String
    public let newBenchScore: Double
    public init(improvementsApplied: String, newBenchScore: Double) {
        self.improvementsApplied = improvementsApplied
        self.newBenchScore = newBenchScore
    }
}

/// Self-debugging / self-improvement loop — run a bench, keep or improve.
public protocol ISelfImprovementLoop: AnyObject {
    func cycle(benchSuiteId: String) async throws -> SelfImprovementVerdict
}

/// Tracks best bench scores + applies improvements. Ported from
/// `TrackingSelfImprovementLoop`. On each cycle it runs the bench; if the new
/// score meets-or-beats the tracked best it records it ("new best" /
/// "no regression"), otherwise it asks for a proposed improvement.
public final class TrackingSelfImprovementLoop: ISelfImprovementLoop, @unchecked Sendable {
    public typealias RunBench = @Sendable (String) async -> Double
    public typealias ProposeImprovement = @Sendable (_ id: String, _ current: Double) async -> String

    private let lock = NSLock()
    private var bestScores: [String: Double] = [:]
    private let runBench: RunBench
    private let proposeImprovement: ProposeImprovement

    public init(runBench: RunBench? = nil, proposeImprovement: ProposeImprovement? = nil) {
        self.runBench = runBench ?? Self.defaultRunBench
        self.proposeImprovement = proposeImprovement ?? Self.defaultProposeImprovement
    }

    public func cycle(benchSuiteId: String) async throws -> SelfImprovementVerdict {
        precondition(!benchSuiteId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "benchSuiteId required")
        lock.lock()
        let baseline = bestScores[benchSuiteId] ?? 0.0
        lock.unlock()

        let current = await runBench(benchSuiteId)
        var applied = "none"
        if current >= baseline {
            lock.lock(); bestScores[benchSuiteId] = current; lock.unlock()
            applied = current > baseline ? "new best" : "no regression"
        } else {
            applied = await proposeImprovement(benchSuiteId, current)
        }
        return SelfImprovementVerdict(improvementsApplied: applied, newBenchScore: current)
    }

    public func bestScoreFor(_ benchSuiteId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        return bestScores[benchSuiteId] ?? 0
    }

    static func defaultRunBench(_ id: String) async -> Double {
        0.5 + Double(StableHash.fnv1a32(id) & 0xFFFF) / 65535.0 * 0.5
    }

    static func defaultProposeImprovement(_ id: String, _ current: Double) async -> String {
        "retry-with-temperature-0 (score was \(String(format: "%.3f", current)))"
    }
}

// =====================================================================
// Shared helpers
// =====================================================================

/// Errors surfaced by the HER/Jarvis in-memory implementations. Mirrors the C#
/// `ArgumentException` / `InvalidOperationException` distinctions where those
/// were thrown (as opposed to `precondition`-style guard clauses).
public enum HerJarvisError: Error, Equatable {
    case invalidArgument(String)
    case invalidOperation(String)
}

/// A stable, non-randomised 32-bit FNV-1a hash — replaces .NET's per-process
/// `String.GetHashCode()` so seeded/derived behaviour is reproducible.
enum StableHash {
    static func fnv1a32(_ s: String) -> UInt32 {
        var hash: UInt32 = 2166136261
        for byte in s.utf8 {
            hash ^= UInt32(byte)
            hash = hash &* 16777619
        }
        return hash
    }
}

/// Formatting helpers matching System.Text.Json / .NET rendering used by the
/// C# reference (JSON string escaping + ISO-8601 round-trip "O" format).
enum NetJson {
    /// JSON-encodes a string the way `JsonSerializer.Serialize(string)` does with
    /// the default encoder (see TheoryOfMind.swift for the full character table).
    static func string(_ s: String) -> String {
        let escapedAscii: Set<Unicode.Scalar> = ["\"", "&", "'", "+", "<", ">", "`"]
        var out = "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\\": out += "\\\\"
            case "\u{08}": out += "\\b"
            case "\t": out += "\\t"
            case "\n": out += "\\n"
            case "\u{0C}": out += "\\f"
            case "\r": out += "\\r"
            default:
                if scalar.value < 0x20 || scalar.value >= 0x7F || escapedAscii.contains(scalar) {
                    out += String(format: "\\u%04X", scalar.value)
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        out += "\""
        return out
    }

    /// Renders a UTC instant in .NET's round-trip "O" format:
    /// `yyyy-MM-ddTHH:mm:ss.fffffffZ` (7 fractional digits, always Z here since
    /// the reference builds these from UTC DateTimeOffsets).
    static func iso8601Round(_ date: Date) -> String {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let c = cal.dateComponents([.year, .month, .day, .hour, .minute, .second, .nanosecond], from: date)
        // .NET "O" prints 7 fractional digits (100-ns ticks).
        let ticks = Int((Double(c.nanosecond ?? 0) / 100.0).rounded())
        return String(format: "%04d-%02d-%02dT%02d:%02d:%02d.%07dZ",
                      c.year ?? 0, c.month ?? 0, c.day ?? 0,
                      c.hour ?? 0, c.minute ?? 0, c.second ?? 0, ticks)
    }
}
