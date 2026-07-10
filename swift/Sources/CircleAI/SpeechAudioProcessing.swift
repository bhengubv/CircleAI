// SpeechAudioProcessing.swift
//
// Port of CircleAI.Speech audio-processing components:
//   - EchoCancellers.cs      -> Null / Nlms / WebRtc (+ IEchoCancellerModelRunner)
//   - NoiseReducers.cs       -> Null / SpectralSubtraction / Krisp / DeepFilterNet
//                               (+ INoiseReducerModelRunner)
//   - VoiceActivityDetectors.cs -> Null / Energy / Silero frame VAD
//                               (+ IVadModelRunner)
//   - EndOfTurnDetectors.cs  -> Null / RuleBased / SmartTurn (+ ITurnModelRunner)
//
// Byte formats and algorithms match the C# exactly: PCM-16 mono, little-endian
// Int16 read/write, normalisation by Int16.max (32767), NLMS circular buffer,
// RMS + zero-crossing-rate + hangover smoothing, punctuation/hanging-word rules.

import Foundation

// =====================================================================
// Little-endian PCM-16 helpers (mirror System.Buffers.Binary.BinaryPrimitives)
// =====================================================================

@inline(__always)
internal func readInt16LE(_ bytes: [UInt8], _ byteOffset: Int) -> Int16 {
    let lo = UInt16(bytes[byteOffset])
    let hi = UInt16(bytes[byteOffset + 1])
    return Int16(bitPattern: lo | (hi << 8))
}

@inline(__always)
internal func writeInt16LE(_ bytes: inout [UInt8], _ byteOffset: Int, _ value: Int16) {
    let u = UInt16(bitPattern: value)
    bytes[byteOffset] = UInt8(u & 0xFF)
    bytes[byteOffset + 1] = UInt8((u >> 8) & 0xFF)
}

/// short.MaxValue / short.MinValue, matching C#.
internal let kInt16Max: Int = 32767
internal let kInt16Min: Int = -32768

// =====================================================================
// Echo cancellers (EchoCancellers.cs)
// =====================================================================

/// Pass-through DI default. Port of `NullEchoCanceller`.
public final class NullEchoCanceller: IEchoCanceller, @unchecked Sendable {
    public static let instance = NullEchoCanceller()
    public init() {}
    public var backendId: String { "null" }

    public func cancel(nearEndMicrophone: [UInt8], farEndReference: [UInt8], sampleRateHz: Int) -> [UInt8] {
        // C#: nearEndMicrophone.CopyTo(destination); return nearEndMicrophone.Length;
        nearEndMicrophone
    }

    public func reset() {}
}

/// Normalised LMS adaptive-filter AEC. Port of `NlmsEchoCanceller`. Pure Swift,
/// filter length defaults to 256 taps.
public final class NlmsEchoCanceller: IEchoCanceller, @unchecked Sendable {
    private var w: [Float]
    private let stepSize: Float
    private let epsilon: Float
    private let filterLength: Int
    private var refBuffer: [Float]
    private var refIndex: Int = 0

    public init(filterLength: Int = 256, stepSize: Float = 0.4, epsilon: Float = 1e-6) {
        self.filterLength = filterLength
        self.stepSize = stepSize
        self.epsilon = epsilon
        self.w = [Float](repeating: 0, count: filterLength)
        self.refBuffer = [Float](repeating: 0, count: filterLength)
    }

    public var backendId: String { "nlms" }

    public func cancel(nearEndMicrophone: [UInt8], farEndReference: [UInt8], sampleRateHz: Int) -> [UInt8] {
        precondition(nearEndMicrophone.count == farEndReference.count,
                     "near-end and far-end must be the same length.")

        var destination = [UInt8](repeating: 0, count: nearEndMicrophone.count)
        let sampleCount = nearEndMicrophone.count / 2

        for n in 0..<sampleCount {
            let micSample = Float(readInt16LE(nearEndMicrophone, n * 2)) / Float(kInt16Max)
            let farSample = Float(readInt16LE(farEndReference, n * 2)) / Float(kInt16Max)

            // Push far-end into circular reference buffer.
            refBuffer[refIndex] = farSample

            // Estimated echo: dot(w, ref).
            var echoEstimate: Float = 0
            var power: Float = epsilon
            for k in 0..<filterLength {
                let rIdx = (refIndex - k + filterLength) % filterLength
                let x = refBuffer[rIdx]
                echoEstimate += w[k] * x
                power += x * x
            }

            // Error = mic - echo estimate.
            let error = micSample - echoEstimate

            // Update filter weights.
            let mu = stepSize / power
            for k in 0..<filterLength {
                let rIdx = (refIndex - k + filterLength) % filterLength
                w[k] += mu * error * refBuffer[rIdx]
            }

            refIndex = (refIndex + 1) % filterLength

            // Clamp + write.
            let scaled = error * Float(kInt16Max)
            let clamped = Int(min(max(scaled, Float(kInt16Min)), Float(kInt16Max)))
            writeInt16LE(&destination, n * 2, Int16(clamped))
        }

        return destination
    }

    public func reset() {
        for i in 0..<w.count { w[i] = 0 }
        for i in 0..<refBuffer.count { refBuffer[i] = 0 }
        refIndex = 0
    }
}

/// Host-supplied AEC model runner (e.g. WebRTC AEC3). Port of
/// `IEchoCancellerModelRunner`.
public protocol IEchoCancellerModelRunner: AnyObject {
    /// Process one frame; returns the cancelled PCM-16 mono bytes.
    func process(nearEnd: [UInt8], farEnd: [UInt8], sampleRateHz: Int) -> [UInt8]
    func reset()
}

/// WebRTC AEC3 wrapper — falls back to NLMS when no runner is wired. Port of
/// `WebRtcEchoCanceller`.
public final class WebRtcEchoCanceller: IEchoCanceller, @unchecked Sendable {
    private let runner: IEchoCancellerModelRunner?
    private let fallback = NlmsEchoCanceller()

    public init(runner: IEchoCancellerModelRunner? = nil) { self.runner = runner }

    public var backendId: String { runner == nil ? "webrtc-aec3 (fallback)" : "webrtc-aec3" }

    public func cancel(nearEndMicrophone: [UInt8], farEndReference: [UInt8], sampleRateHz: Int) -> [UInt8] {
        if let runner {
            return runner.process(nearEnd: nearEndMicrophone, farEnd: farEndReference, sampleRateHz: sampleRateHz)
        }
        return fallback.cancel(nearEndMicrophone: nearEndMicrophone, farEndReference: farEndReference, sampleRateHz: sampleRateHz)
    }

    public func reset() {
        fallback.reset()
        runner?.reset()
    }
}

// =====================================================================
// Noise reducers (NoiseReducers.cs)
// =====================================================================

/// No-op reducer — DI default. Port of `NullNoiseReducer`.
public final class NullNoiseReducer: INoiseReducer, @unchecked Sendable {
    public static let instance = NullNoiseReducer()
    public init() {}
    public var backendId: String { "null" }
    public var isAvailable: Bool { true }

    public func reduce(audioPcm16Mono: [UInt8], sampleRateHz: Int) -> [UInt8] {
        audioPcm16Mono
    }
}

/// Lightweight time-domain noise gate. Port of `SpectralSubtractionNoiseReducer`.
/// Attenuates samples whose |value| <= floor with a soft knee.
public final class SpectralSubtractionNoiseReducer: INoiseReducer, @unchecked Sendable {
    private let floorEstimate: Float
    private let attenuation: Float

    public init(floorEstimate: Float = 0.008, attenuation: Float = 0.25) {
        self.floorEstimate = floorEstimate
        self.attenuation = attenuation
    }

    public var backendId: String { "passthrough" }
    public var isAvailable: Bool { true }

    public func reduce(audioPcm16Mono: [UInt8], sampleRateHz: Int) -> [UInt8] {
        var destination = [UInt8](repeating: 0, count: audioPcm16Mono.count)
        let count = audioPcm16Mono.count / 2
        let floor = Int(floorEstimate * Float(kInt16Max))

        for i in 0..<count {
            let s = Int(readInt16LE(audioPcm16Mono, i * 2))
            let absVal = abs(s)
            if absVal <= floor {
                // dst[i] = (short)(s * attenuation);  — C# truncates toward zero.
                let attenuated = Int16(truncatingIfNeeded: Int(Float(s) * attenuation))
                writeInt16LE(&destination, i * 2, attenuated)
            } else {
                writeInt16LE(&destination, i * 2, Int16(truncatingIfNeeded: s))
            }
        }
        return destination
    }
}

/// Host-supplied DNN runner for noise reduction. Port of `INoiseReducerModelRunner`.
public protocol INoiseReducerModelRunner: AnyObject {
    /// Process one frame; returns cleaned PCM-16 mono bytes.
    func process(audioPcm16Mono: [UInt8], sampleRateHz: Int) -> [UInt8]
}

/// Krisp wrapper — uses the host's runner when present. Port of `KrispNoiseReducer`.
public final class KrispNoiseReducer: INoiseReducer, @unchecked Sendable {
    private let runner: INoiseReducerModelRunner?
    private let fallback = SpectralSubtractionNoiseReducer()

    public init(runner: INoiseReducerModelRunner? = nil) { self.runner = runner }

    public var backendId: String { runner == nil ? "krisp (fallback)" : "krisp" }
    public var isAvailable: Bool { true }

    public func reduce(audioPcm16Mono: [UInt8], sampleRateHz: Int) -> [UInt8] {
        if let runner { return runner.process(audioPcm16Mono: audioPcm16Mono, sampleRateHz: sampleRateHz) }
        return fallback.reduce(audioPcm16Mono: audioPcm16Mono, sampleRateHz: sampleRateHz)
    }
}

/// DeepFilterNet wrapper. Port of `DeepFilterNetNoiseReducer`.
public final class DeepFilterNetNoiseReducer: INoiseReducer, @unchecked Sendable {
    private let runner: INoiseReducerModelRunner?
    private let fallback = SpectralSubtractionNoiseReducer()

    public init(runner: INoiseReducerModelRunner? = nil) { self.runner = runner }

    public var backendId: String { runner == nil ? "deepfilternet (fallback)" : "deepfilternet" }
    public var isAvailable: Bool { true }

    public func reduce(audioPcm16Mono: [UInt8], sampleRateHz: Int) -> [UInt8] {
        if let runner { return runner.process(audioPcm16Mono: audioPcm16Mono, sampleRateHz: sampleRateHz) }
        return fallback.reduce(audioPcm16Mono: audioPcm16Mono, sampleRateHz: sampleRateHz)
    }
}

// =====================================================================
// Frame voice-activity detectors (VoiceActivityDetectors.cs)
// =====================================================================

/// Always reports speech — DI default. Port of `NullVoiceActivityDetector`
/// (frame-based).
public final class NullFrameVoiceActivityDetector: IFrameVoiceActivityDetector, @unchecked Sendable {
    public static let instance = NullFrameVoiceActivityDetector()
    public init() {}
    public var backendId: String { "null" }
    public var speechThreshold: Float { 0.5 }

    public func classify(audioPcm16Mono: [UInt8], sampleRateHz: Int, offset: TimeInterval) -> VadFrameResult {
        VadFrameResult(isSpeech: true, speechProbability: 1, offset: offset)
    }

    public func reset() {}
}

/// Production-grade VAD using RMS energy + zero-crossing rate + hangover-frame
/// smoothing. Port of `EnergyVoiceActivityDetector`.
public final class EnergyVoiceActivityDetector: IFrameVoiceActivityDetector, @unchecked Sendable {
    private let energyThreshold: Float
    private let hangoverFrames: Int
    private var hangoverRemaining: Int = 0
    public let speechThreshold: Float

    public init(speechThreshold: Float = 0.55, energyThreshold: Float = 0.012, hangoverFrames: Int = 8) {
        self.speechThreshold = speechThreshold
        self.energyThreshold = energyThreshold
        self.hangoverFrames = hangoverFrames
    }

    public var backendId: String { "energy" }

    public func classify(audioPcm16Mono: [UInt8], sampleRateHz: Int, offset: TimeInterval) -> VadFrameResult {
        if audioPcm16Mono.count < 2 {
            return VadFrameResult(isSpeech: false, speechProbability: 0, offset: offset)
        }

        let sampleCount = audioPcm16Mono.count / 2
        var sumSquares: Double = 0
        var zeroCrossings = 0
        var previous: Int16 = 0
        for i in 0..<sampleCount {
            let s = readInt16LE(audioPcm16Mono, i * 2)
            let si = Int(s)
            sumSquares += Double(si) * Double(si)
            if i > 0 && sign(si) != sign(Int(previous)) && si != 0 && previous != 0 {
                zeroCrossings += 1
            }
            previous = s
        }
        let rms = (sumSquares / Double(sampleCount)).squareRoot() / Double(kInt16Max) // 0..1
        let zcrRate = Float(zeroCrossings) / Float(sampleCount)

        // Speech: high RMS + moderate ZCR (~0.05–0.25 for voiced speech).
        let energyGood = Float(rms) >= energyThreshold
        let zcrGood = zcrRate >= 0.02 && zcrRate <= 0.30
        var rawProb: Float = energyGood ? (zcrGood ? 0.85 : 0.6) : 0.1

        let isSpeech: Bool
        if rawProb >= speechThreshold {
            isSpeech = true
            hangoverRemaining = hangoverFrames
        } else if hangoverRemaining > 0 {
            isSpeech = true
            hangoverRemaining -= 1
            rawProb = max(rawProb, speechThreshold)
        } else {
            isSpeech = false
        }

        return VadFrameResult(isSpeech: isSpeech, speechProbability: rawProb, offset: offset)
    }

    public func reset() { hangoverRemaining = 0 }

    /// Math.Sign parity (returns -1 / 0 / +1).
    @inline(__always)
    private func sign(_ v: Int) -> Int { v > 0 ? 1 : (v < 0 ? -1 : 0) }
}

/// ONNX model runner contract supplied by the host package. Port of `IVadModelRunner`.
public protocol IVadModelRunner: AnyObject {
    /// Score one 30 ms / 16 kHz PCM-16 frame; result is 0..1.
    func scoreFrame(audioPcm16Mono: [UInt8], sampleRateHz: Int) -> Float
}

/// Silero VAD wrapper. Delegates per-frame score to a host runner; falls back to
/// the energy detector's scoring when no runner is wired. Port of
/// `SileroVoiceActivityDetector`.
public final class SileroVoiceActivityDetector: IFrameVoiceActivityDetector, @unchecked Sendable {
    private let runner: IVadModelRunner?
    private let fallback: EnergyVoiceActivityDetector
    private let hangoverFrames: Int
    private var hangoverRemaining: Int = 0
    public let speechThreshold: Float

    public init(runner: IVadModelRunner? = nil, speechThreshold: Float = 0.5, hangoverFrames: Int = 8) {
        self.runner = runner
        self.fallback = EnergyVoiceActivityDetector(speechThreshold: speechThreshold)
        self.speechThreshold = speechThreshold
        self.hangoverFrames = hangoverFrames
    }

    public var backendId: String { runner == nil ? "silero (fallback)" : "silero" }

    public func classify(audioPcm16Mono: [UInt8], sampleRateHz: Int, offset: TimeInterval) -> VadFrameResult {
        guard let runner else {
            return fallback.classify(audioPcm16Mono: audioPcm16Mono, sampleRateHz: sampleRateHz, offset: offset)
        }

        let prob = runner.scoreFrame(audioPcm16Mono: audioPcm16Mono, sampleRateHz: sampleRateHz)
        let isSpeech: Bool
        if prob >= speechThreshold {
            isSpeech = true
            hangoverRemaining = hangoverFrames
        } else if hangoverRemaining > 0 {
            isSpeech = true
            hangoverRemaining -= 1
        } else {
            isSpeech = false
        }
        return VadFrameResult(isSpeech: isSpeech, speechProbability: prob, offset: offset)
    }

    public func reset() {
        hangoverRemaining = 0
        fallback.reset()
    }
}

// =====================================================================
// End-of-turn detectors (EndOfTurnDetectors.cs)
// =====================================================================

/// Always says "they finished" — DI default. Port of `NullEndOfTurnDetector`.
public final class NullEndOfTurnDetector: IEndOfTurnDetector, @unchecked Sendable {
    public static let instance = NullEndOfTurnDetector()
    public init() {}
    public var backendId: String { "null" }

    public func predict(partialTranscript: String, trailingSilence: TimeInterval) -> EndOfTurnResult {
        EndOfTurnResult(isComplete: true, confidence: 1, waitMoreMs: 0)
    }

    public func reset() {}
}

/// Rule-based detector using punctuation + trailing-silence heuristics. Port of
/// `RuleBasedEndOfTurnDetector`.
public final class RuleBasedEndOfTurnDetector: IEndOfTurnDetector, @unchecked Sendable {
    private static let terminalPunctuation: [String] = [".", "!", "?", "。", "！", "？"]
    private static let hangingWords: Set<String> = [
        "and", "but", "so", "or", "because", "if", "when", "while",
        "though", "however", "um", "uh", "like", "you", "the", "a", "an",
    ]

    private let minSilence: TimeInterval
    private let hangingSilence: TimeInterval
    private let maxSilence: TimeInterval

    public init(minSilence: TimeInterval? = nil, hangingSilence: TimeInterval? = nil, maxSilence: TimeInterval? = nil) {
        // C# defaults expressed in milliseconds; Swift TimeInterval is seconds.
        self.minSilence = minSilence ?? 0.400
        self.hangingSilence = hangingSilence ?? 0.900
        self.maxSilence = maxSilence ?? 2.500
    }

    public var backendId: String { "rules" }

    public func predict(partialTranscript: String, trailingSilence: TimeInterval) -> EndOfTurnResult {
        let text = partialTranscript.trimmingCharacters(in: .whitespacesAndNewlines)
        if trailingSilence >= maxSilence {
            return EndOfTurnResult(isComplete: true, confidence: 0.7, waitMoreMs: 0)
        }

        if text.isEmpty {
            let ms = Int(max(150.0, (minSilence - trailingSilence) * 1000.0))
            return EndOfTurnResult(isComplete: false, confidence: 0.2, waitMoreMs: ms)
        }

        let endsTerminal = Self.terminalPunctuation.contains { text.hasSuffix($0) }
        let words = text.split(whereSeparator: { $0 == " " || $0 == "\t" || $0 == "\n" })
        let lastWordRaw = words.last.map(String.init) ?? ""
        // TrimEnd('.', ',', '!', '?').ToLowerInvariant()
        let trimmedLast = String(lastWordRaw.reversed().drop(while: { ".,!?".contains($0) }).reversed())
        let endsHanging = Self.hangingWords.contains(trimmedLast.lowercased())

        if endsHanging {
            let remaining = hangingSilence - trailingSilence
            if remaining <= 0 {
                return EndOfTurnResult(isComplete: true, confidence: 0.6, waitMoreMs: 0)
            }
            // (int)Math.Ceiling(remaining.TotalMilliseconds)
            let ms = Int((remaining * 1000.0).rounded(.up))
            return EndOfTurnResult(isComplete: false, confidence: 0.4, waitMoreMs: ms)
        }

        if endsTerminal && trailingSilence >= minSilence {
            return EndOfTurnResult(isComplete: true, confidence: 0.9, waitMoreMs: 0)
        }

        if trailingSilence >= minSilence {
            return EndOfTurnResult(isComplete: true, confidence: 0.75, waitMoreMs: 0)
        }

        let ms = Int(max(50.0, (minSilence - trailingSilence) * 1000.0))
        return EndOfTurnResult(isComplete: false, confidence: 0.6, waitMoreMs: ms)
    }

    public func reset() {}
}

/// Host-supplied semantic turn model. Port of `ITurnModelRunner`.
public protocol ITurnModelRunner: AnyObject {
    /// Score the current state; 0..1 = probability the turn is complete.
    func scoreCompletion(partialTranscript: String, trailingSilence: TimeInterval) -> Float
}

/// Smart-turn wrapper. Uses the supplied model when present; otherwise falls
/// back to the rule-based detector. Port of `SmartTurnDetector`.
public final class SmartTurnDetector: IEndOfTurnDetector, @unchecked Sendable {
    private let runner: ITurnModelRunner?
    private let fallback = RuleBasedEndOfTurnDetector()
    private let threshold: Float

    public init(runner: ITurnModelRunner? = nil, threshold: Float = 0.5) {
        self.runner = runner
        self.threshold = threshold
    }

    public var backendId: String { runner == nil ? "smart-turn (fallback)" : "smart-turn-v2" }

    public func predict(partialTranscript: String, trailingSilence: TimeInterval) -> EndOfTurnResult {
        guard let runner else {
            return fallback.predict(partialTranscript: partialTranscript, trailingSilence: trailingSilence)
        }

        let prob = min(max(runner.scoreCompletion(partialTranscript: partialTranscript, trailingSilence: trailingSilence), 0), 1)
        if prob >= threshold {
            return EndOfTurnResult(isComplete: true, confidence: prob, waitMoreMs: 0)
        }
        // (int)Math.Round((1f - prob) * 1000f) — C# banker's rounding.
        let waitMs = Int((Double(1 - prob) * 1000.0).rounded(.toNearestOrEven))
        return EndOfTurnResult(isComplete: false, confidence: prob, waitMoreMs: waitMs)
    }

    public func reset() { fallback.reset() }
}
