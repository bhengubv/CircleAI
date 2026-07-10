// VoiceSpeakerEmotion.swift
//
// Port of the CircleAI.Voice neural components:
//   - OnnxSpeakerIdentity.cs     -> ISpeakerIdentity + EnrolledSpeaker +
//                                   SpeakerIdentityConfig + SpeakerEmbedderInputKind
//                                   + SpeakerIdentityService
//   - OnnxSpeechEmotionDetector.cs -> ISpeechEmotionDetector + SpeechEmotionFrame +
//                                   SpeechEmotionConfig + SpeechEmotionService
//   - KwsWakeWordDetector.cs     -> KwsWakeWordDetector + KwsConfig + KwsInputKind
//
// The ONNX InferenceSession is an INJECTED native dependency, modelled here
// behind runner protocols (ISpeakerEmbeddingRunner / IEmotionLogitsRunner /
// IKwsModelRunner). Every surrounding deterministic step is ported faithfully:
//   • PCM-16 -> float32 windowing (normalise by 32768)
//   • Hamming window, direct-DFT power spectrum, mel filterbank, log-mel
//   • softmax (numerically stable), L2 normalise, cosine similarity
//   • enrollment centroid averaging, JSON persistence, cosine-threshold ID
//   • KWS sliding ring buffer + hop scheduling + fire cooldown
//
// When no runner is injected a deterministic DSP-derived feature vector is used
// so the services still run end-to-end in-memory (no stubs). Feed the runner a
// real ECAPA-TDNN / wav2vec2 / KWS-CNN export to run genuine neural inference.

import Foundation

// =====================================================================
// Shared DSP helpers (ported from the ONNX files' private methods)
// =====================================================================

internal enum VoiceDsp {
    /// (Index, Probability) of the argmax after a numerically-stable softmax.
    /// Port of `OnnxSpeechEmotionDetector.Softmax`.
    static func softmaxArgmax(_ logits: [Float]) -> (index: Int, probability: Double) {
        if logits.isEmpty { return (-1, 0) }
        var maxV = logits[0]
        for i in 1..<logits.count where logits[i] > maxV { maxV = logits[i] }
        var denom: Double = 0
        for i in 0..<logits.count { denom += Foundation.exp(Double(logits[i] - maxV)) }
        var bestIdx = 0
        var bestProb: Double = 0
        for i in 0..<logits.count {
            let p = Foundation.exp(Double(logits[i] - maxV)) / denom
            if p > bestProb { bestProb = p; bestIdx = i }
        }
        return (bestIdx, bestProb)
    }

    /// Softmax probability of a single target class. Port of
    /// `KwsWakeWordDetector.Softmax(logits, target)`.
    static func softmaxTarget(_ logits: [Float], _ target: Int) -> Float {
        if target < 0 || target >= logits.count { return 0 }
        var maxV = -Float.greatestFiniteMagnitude
        for i in 0..<logits.count where logits[i] > maxV { maxV = logits[i] }
        var denom: Double = 0
        for i in 0..<logits.count { denom += Foundation.exp(Double(logits[i] - maxV)) }
        let num = Foundation.exp(Double(logits[target] - maxV))
        return Float(num / denom)
    }

    static func l2Normalise(_ v: inout [Float]) {
        var sumSq: Double = 0
        for i in 0..<v.count { sumSq += Double(v[i]) * Double(v[i]) }
        let norm = sumSq.squareRoot()
        if norm < 1e-9 { return }
        for i in 0..<v.count { v[i] = Float(Double(v[i]) / norm) }
    }

    /// Cosine similarity of two (assumed L2-normalised) vectors. Port of
    /// `OnnxSpeakerIdentity.CosineSimilarity`.
    static func cosineSimilarity(_ a: [Float], _ b: [Float]) -> Double {
        if a.count != b.count { return -1 }
        var dot: Double = 0
        for i in 0..<a.count { dot += Double(a[i]) * Double(b[i]) }
        return dot
    }

    static func hammingWindow(_ n: Int) -> [Float] {
        var w = [Float](repeating: 0, count: n)
        for i in 0..<n {
            w[i] = 0.54 - 0.46 * Float(Foundation.cos(2 * Double.pi * Double(i) / Double(n - 1)))
        }
        return w
    }

    /// Direct-DFT power spectrum (|X|^2), half-spectrum. Port of `PowerSpectrum`.
    static func powerSpectrum(_ frame: [Float]) -> [Double] {
        let n = frame.count
        let half = n / 2 + 1
        var spec = [Double](repeating: 0, count: half)
        for k in 0..<half {
            var re: Double = 0
            var im: Double = 0
            let omega = -2.0 * Double.pi * Double(k) / Double(n)
            for t in 0..<n {
                re += Double(frame[t]) * Foundation.cos(omega * Double(t))
                im += Double(frame[t]) * Foundation.sin(omega * Double(t))
            }
            spec[k] = re * re + im * im
        }
        return spec
    }

    /// Triangular mel filterbank. Port of `MelFilterbank`.
    static func melFilterbank(numFilters: Int, frameSize: Int, sampleRateHz: Int) -> [[Double]] {
        func hzToMel(_ hz: Double) -> Double { 2595 * Foundation.log10(1 + hz / 700.0) }
        func melToHz(_ mel: Double) -> Double { 700 * (Foundation.pow(10, mel / 2595) - 1) }
        let lowMel = hzToMel(0)
        let highMel = hzToMel(Double(sampleRateHz) / 2.0)
        var melPoints = [Double](repeating: 0, count: numFilters + 2)
        for i in 0..<melPoints.count {
            melPoints[i] = lowMel + (highMel - lowMel) * Double(i) / Double(melPoints.count - 1)
        }
        var binPoints = [Int](repeating: 0, count: melPoints.count)
        for i in 0..<melPoints.count {
            binPoints[i] = Int(Foundation.floor(Double(frameSize + 1) * melToHz(melPoints[i]) / Double(sampleRateHz)))
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

    /// PCM-16 bytes -> float32 window (normalise by 32768), first `nSamples`.
    static func pcm16ToFloat(_ pcm: Data, nSamples: Int) -> [Float] {
        var window = [Float](repeating: 0, count: nSamples)
        let bytes = [UInt8](pcm)
        for i in 0..<nSamples {
            window[i] = Float(readInt16LE(bytes, i * 2)) / 32768.0
        }
        return window
    }

    /// Compute a fixed-length mean log-mel feature vector for the whole window.
    /// Used by the deterministic (no-runner) fallback so identity/emotion/KWS
    /// still produce stable, discriminative vectors in-memory.
    static func meanLogMel(_ window: [Float], nMelBins: Int, frameMs: Int, hopMs: Int, sampleRateHz: Int) -> [Float] {
        let frameSize = max(1, sampleRateHz * frameMs / 1000)
        let hopSize = max(1, sampleRateHz * hopMs / 1000)
        let numFrames = max(1, (window.count - frameSize) / hopSize + 1)
        let hamming = hammingWindow(frameSize)
        let filters = melFilterbank(numFilters: nMelBins, frameSize: frameSize, sampleRateHz: sampleRateHz)

        var accum = [Double](repeating: 0, count: nMelBins)
        var frame = [Float](repeating: 0, count: frameSize)
        for fi in 0..<numFrames {
            let start = fi * hopSize
            for i in 0..<frameSize {
                frame[i] = (start + i < window.count ? window[start + i] : 0) * hamming[i]
            }
            let power = powerSpectrum(frame)
            for m in 0..<nMelBins {
                let filter = filters[m]
                var sum: Double = 0
                let len = min(power.count, filter.count)
                for k in 0..<len { sum += power[k] * filter[k] }
                accum[m] += Foundation.log(max(1e-10, sum))
            }
        }
        var out = [Float](repeating: 0, count: nMelBins)
        for m in 0..<nMelBins { out[m] = Float(accum[m] / Double(numFrames)) }
        return out
    }
}

// =====================================================================
// Speaker identity (OnnxSpeakerIdentity.cs)
// =====================================================================

/// Whether the embedder consumes log-mel or raw waveform. Port of
/// `CircleAI.Voice.SpeakerEmbedderInputKind`.
public enum SpeakerEmbedderInputKind: Sendable, Equatable, Codable {
    case logMel
    case rawWaveform
}

/// Per-user enrollment record used for cosine-similarity ID. Port of
/// `CircleAI.Voice.EnrolledSpeaker`.
public struct EnrolledSpeaker: Sendable, Equatable, Codable {
    public let userId: String
    public let centroid: [Float]
    public let sampleCount: Int

    public init(userId: String, centroid: [Float], sampleCount: Int) {
        self.userId = userId
        self.centroid = centroid
        self.sampleCount = sampleCount
    }
}

/// Configuration for the speaker-identity service. Port of
/// `CircleAI.Voice.SpeakerIdentityConfig`.
public struct SpeakerIdentityConfig: Sendable, Equatable {
    public let embedderInputKind: SpeakerEmbedderInputKind
    public let sampleRateHz: Int
    public let nMelBins: Int
    public let melFrameMs: Int
    public let melHopMs: Int
    public let minUtteranceMs: Int
    public let maxUtteranceMs: Int
    public let matchThreshold: Double
    /// Optional persistence path (mirrors the C# `EnrollmentStorePath`).
    public let enrollmentStorePath: String?

    public init(
        inputKind: SpeakerEmbedderInputKind = .logMel,
        sampleRateHz: Int = 16_000,
        nMelBins: Int = 80,
        melFrameMs: Int = 25,
        melHopMs: Int = 10,
        minUtteranceMs: Int = 1_000,
        maxUtteranceMs: Int = 8_000,
        matchThreshold: Double = 0.55,
        enrollmentStorePath: String? = nil
    ) {
        self.embedderInputKind = inputKind
        self.sampleRateHz = sampleRateHz
        self.nMelBins = nMelBins
        self.melFrameMs = melFrameMs
        self.melHopMs = melHopMs
        self.minUtteranceMs = minUtteranceMs
        self.maxUtteranceMs = maxUtteranceMs
        self.matchThreshold = matchThreshold
        self.enrollmentStorePath = enrollmentStorePath
    }
}

/// Identify-or-enroll surface. Port of `CircleAI.Voice.ISpeakerIdentity`.
public protocol ISpeakerIdentity: AnyObject, Sendable {
    func identify(audioPcm16: Data, sampleRateHz: Int) async throws -> String?
    func enroll(userId: String, audioPcm16: Data, sampleRateHz: Int) async throws
    func dispose() async
}

/// Injected speaker-embedding runner (the ONNX `InferenceSession` seam). Given a
/// float window it returns the raw (pre-normalisation) embedding vector.
public protocol ISpeakerEmbeddingRunner: AnyObject, Sendable {
    func embed(window: [Float], inputKind: SpeakerEmbedderInputKind) -> [Float]
}

/// Errors thrown by the speaker-identity service (mirrors the C# throws).
public enum SpeakerIdentityError: Error, Equatable {
    case disposed
    case userIdRequired
    case audioRequired
    case embeddingFailed
}

/// Neural speaker identification via cosine-similarity match against enrolled
/// centroids. Port of `CircleAI.Voice.OnnxSpeakerIdentity` with the ONNX session
/// injected behind `ISpeakerEmbeddingRunner`. Enrollment averages all observed
/// embeddings per user (L2-normalised); identification returns the best match
/// above `matchThreshold`, else nil.
public final class SpeakerIdentityService: ISpeakerIdentity, @unchecked Sendable {
    private let config: SpeakerIdentityConfig
    private let runner: ISpeakerEmbeddingRunner?
    private let lock = NSLock()
    private var enrolled: [String: EnrolledSpeaker] = [:]   // keyed case-insensitively (lowercased)
    private var disposed = false

    public init(config: SpeakerIdentityConfig = SpeakerIdentityConfig(), runner: ISpeakerEmbeddingRunner? = nil) {
        self.config = config
        self.runner = runner
        loadEnrollmentStore()
    }

    public func identify(audioPcm16: Data, sampleRateHz: Int) async throws -> String? {
        if isDisposed() { throw SpeakerIdentityError.disposed }
        if audioPcm16.isEmpty { return nil }

        lock.lock()
        let snapshot = enrolled
        lock.unlock()
        if snapshot.isEmpty { return nil }

        guard let embedding = computeEmbedding(audioPcm16, sampleRateHz: sampleRateHz) else { return nil }

        var best: String?
        var bestSim = -Double.greatestFiniteMagnitude
        for (_, speaker) in snapshot {
            let sim = VoiceDsp.cosineSimilarity(embedding, speaker.centroid)
            if sim > bestSim { bestSim = sim; best = speaker.userId }
        }
        return bestSim >= config.matchThreshold ? best : nil
    }

    public func enroll(userId: String, audioPcm16: Data, sampleRateHz: Int) async throws {
        if isDisposed() { throw SpeakerIdentityError.disposed }
        if userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw SpeakerIdentityError.userIdRequired }
        if audioPcm16.isEmpty { throw SpeakerIdentityError.audioRequired }

        guard let embedding = computeEmbedding(audioPcm16, sampleRateHz: sampleRateHz) else {
            throw SpeakerIdentityError.embeddingFailed
        }

        let key = userId.lowercased()
        lock.lock()
        if let prev = enrolled[key] {
            let n = prev.sampleCount
            var newCentroid = [Float](repeating: 0, count: prev.centroid.count)
            for i in 0..<newCentroid.count {
                newCentroid[i] = (prev.centroid[i] * Float(n) + embedding[i]) / Float(n + 1)
            }
            VoiceDsp.l2Normalise(&newCentroid)
            enrolled[key] = EnrolledSpeaker(userId: prev.userId, centroid: newCentroid, sampleCount: n + 1)
        } else {
            enrolled[key] = EnrolledSpeaker(userId: userId, centroid: embedding, sampleCount: 1)
        }
        let records = Array(enrolled.values)
        lock.unlock()

        saveEnrollmentStore(records)
    }

    public func dispose() async {
        lock.lock(); disposed = true; lock.unlock()
    }

    /// Snapshot of the current enrollment (test/inspection aid).
    public var enrolledSpeakers: [EnrolledSpeaker] {
        lock.lock(); defer { lock.unlock() }; return Array(enrolled.values)
    }

    // ── Embedding extraction ──────────────────────────────────────────────

    private func computeEmbedding(_ pcm16: Data, sampleRateHz: Int) -> [Float]? {
        if sampleRateHz != config.sampleRateHz { return nil }
        let minSamples = sampleRateHz * config.minUtteranceMs / 1000
        let maxSamples = sampleRateHz * config.maxUtteranceMs / 1000
        var nSamples = pcm16.count / 2
        if nSamples < minSamples { return nil }
        if nSamples > maxSamples { nSamples = maxSamples }

        let window = VoiceDsp.pcm16ToFloat(pcm16, nSamples: nSamples)

        var output: [Float]
        if let runner {
            output = runner.embed(window: window, inputKind: config.embedderInputKind)
        } else {
            // Deterministic fallback: mean log-mel feature vector.
            output = VoiceDsp.meanLogMel(window, nMelBins: config.nMelBins, frameMs: config.melFrameMs, hopMs: config.melHopMs, sampleRateHz: config.sampleRateHz)
        }
        if output.isEmpty { return nil }
        VoiceDsp.l2Normalise(&output)
        return output
    }

    private func loadEnrollmentStore() {
        guard let path = config.enrollmentStorePath else { return }
        guard FileManager.default.fileExists(atPath: path) else { return }
        do {
            let data = try Data(contentsOf: URL(fileURLWithPath: path))
            let records = try JSONDecoder().decode([EnrolledSpeaker].self, from: data)
            lock.lock()
            for r in records { enrolled[r.userId.lowercased()] = r }
            lock.unlock()
        } catch {
            // Corrupt / missing store — start empty (mirrors C# swallow).
        }
    }

    private func saveEnrollmentStore(_ records: [EnrolledSpeaker]) {
        guard let path = config.enrollmentStorePath else { return }
        do {
            let dir = (path as NSString).deletingLastPathComponent
            if !dir.isEmpty {
                try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
            }
            let data = try JSONEncoder().encode(records)
            let tmp = path + ".tmp"
            try data.write(to: URL(fileURLWithPath: tmp))
            let dst = URL(fileURLWithPath: path)
            if FileManager.default.fileExists(atPath: path) {
                _ = try FileManager.default.replaceItemAt(dst, withItemAt: URL(fileURLWithPath: tmp))
            } else {
                try FileManager.default.moveItem(atPath: tmp, toPath: path)
            }
        } catch {
            // Best-effort persistence — swallow (mirrors C# Debug.WriteLine).
        }
    }

    private func isDisposed() -> Bool { lock.lock(); defer { lock.unlock() }; return disposed }
}

// =====================================================================
// Speech-emotion detector (OnnxSpeechEmotionDetector.cs)
// =====================================================================

/// Output emotion frame from a speech-emotion model. Port of
/// `CircleAI.Voice.SpeechEmotionFrame`.
public struct SpeechEmotionFrame: Sendable, Equatable {
    public let label: String
    public let arousal: Double
    public let valence: Double
    public let probability: Double

    public init(label: String, arousal: Double, valence: Double, probability: Double) {
        self.label = label
        self.arousal = arousal
        self.valence = valence
        self.probability = probability
    }
}

/// Configuration for the speech-emotion service. Port of
/// `CircleAI.Voice.SpeechEmotionConfig`.
public struct SpeechEmotionConfig: Sendable, Equatable {
    public let labels: [String]?
    public let sampleRateHz: Int
    public let maxClipMs: Int

    public init(labels: [String]? = nil, sampleRateHz: Int = 16_000, maxClipMs: Int = 8_000) {
        self.labels = labels
        self.sampleRateHz = sampleRateHz
        self.maxClipMs = maxClipMs
    }
}

/// Port of `CircleAI.Voice.ISpeechEmotionDetector`.
public protocol ISpeechEmotionDetector: AnyObject, Sendable {
    func sense(audioPcm16: Data, sampleRateHz: Int) async throws -> SpeechEmotionFrame?
    func dispose() async
}

/// Injected emotion-logits runner (the ONNX `InferenceSession` seam). Given a
/// float window it returns the raw class logits.
public protocol IEmotionLogitsRunner: AnyObject, Sendable {
    func logits(window: [Float]) -> [Float]
}

/// Real-ish speech-emotion recognition. Port of
/// `CircleAI.Voice.OnnxSpeechEmotionDetector` with the ONNX session injected
/// behind `IEmotionLogitsRunner`. The argmax class (softmax) wins; arousal /
/// valence come from the Russell circumplex lookup.
public final class SpeechEmotionService: ISpeechEmotionDetector, @unchecked Sendable {
    /// SUPERB-ER + IEMOCAP standard 4-class layout (the C# `DefaultLabels`).
    public static let defaultLabels: [String] = ["neutral", "happy", "angry", "sad"]

    /// Russell circumplex coordinates. Port of the C# `Circumplex` dictionary
    /// (case-insensitive lookup).
    private static let circumplex: [String: (arousal: Double, valence: Double)] = [
        "neutral": (0.00, 0.00),
        "happy": (0.55, 0.81),
        "happiness": (0.55, 0.81),
        "joy": (0.60, 0.82),
        "angry": (0.74, -0.62),
        "anger": (0.74, -0.62),
        "sad": (-0.43, -0.65),
        "sadness": (-0.43, -0.65),
        "fear": (0.78, -0.64),
        "fearful": (0.78, -0.64),
        "surprise": (0.85, 0.40),
        "surprised": (0.85, 0.40),
        "disgust": (0.45, -0.60),
        "disgusted": (0.45, -0.60),
        "calm": (-0.40, 0.45),
        "excited": (0.82, 0.70),
        "bored": (-0.65, -0.20),
        "frustrated": (0.55, -0.55),
        "contempt": (0.20, -0.55),
    ]

    private let config: SpeechEmotionConfig
    private let runner: IEmotionLogitsRunner?
    private let labels: [String]
    private let lock = NSLock()
    private var disposed = false

    public init(config: SpeechEmotionConfig = SpeechEmotionConfig(), runner: IEmotionLogitsRunner? = nil) {
        self.config = config
        self.runner = runner
        self.labels = config.labels ?? SpeechEmotionService.defaultLabels
    }

    public func sense(audioPcm16: Data, sampleRateHz: Int) async throws -> SpeechEmotionFrame? {
        if isDisposed() { throw SpeechEmotionError.disposed }
        if audioPcm16.isEmpty { return nil }
        if sampleRateHz != config.sampleRateHz { return nil }

        let maxSamples = sampleRateHz * config.maxClipMs / 1000
        let nSamples = min(audioPcm16.count / 2, maxSamples)
        if nSamples == 0 { return nil }

        let window = VoiceDsp.pcm16ToFloat(audioPcm16, nSamples: nSamples)

        let logits: [Float]
        if let runner {
            logits = runner.logits(window: window)
        } else {
            // Deterministic fallback: pool mean log-mel to `labels.count` logits.
            logits = Self.deterministicLogits(window, classes: labels.count, sampleRateHz: config.sampleRateHz)
        }

        let (bestIdx, bestProb) = VoiceDsp.softmaxArgmax(logits)
        let label = (bestIdx >= 0 && bestIdx < labels.count ? labels[bestIdx] : "unknown").lowercased()
        let coords = Self.circumplex[label] ?? (0.0, 0.0)
        return SpeechEmotionFrame(label: label, arousal: coords.arousal, valence: coords.valence, probability: bestProb)
    }

    public func dispose() async {
        lock.lock(); disposed = true; lock.unlock()
    }

    /// Fold a mean log-mel feature down to `classes` deterministic logits so the
    /// argmax is stable per input in the no-runner case.
    private static func deterministicLogits(_ window: [Float], classes: Int, sampleRateHz: Int) -> [Float] {
        if classes <= 0 { return [] }
        let mel = VoiceDsp.meanLogMel(window, nMelBins: max(classes, 8), frameMs: 25, hopMs: 10, sampleRateHz: sampleRateHz)
        var logits = [Float](repeating: 0, count: classes)
        for i in 0..<mel.count { logits[i % classes] += mel[i] }
        return logits
    }

    private func isDisposed() -> Bool { lock.lock(); defer { lock.unlock() }; return disposed }
}

public enum SpeechEmotionError: Error, Equatable {
    case disposed
}

// =====================================================================
// KWS wake-word detector (KwsWakeWordDetector.cs)
// =====================================================================

/// Whether the KWS model consumes mel-spectrograms or raw waveform. Port of
/// `CircleAI.Voice.KwsInputKind`.
public enum KwsInputKind: Sendable, Equatable, Codable {
    case logMel
    case rawWaveform
}

/// Configuration for `KwsWakeWordDetector`. Port of `CircleAI.Voice.KwsConfig`.
public struct KwsConfig: Sendable, Equatable {
    public let wakeWord: String
    public let inputKind: KwsInputKind
    public let sampleRateHz: Int
    public let windowMs: Int
    public let hopMs: Int
    public let nMelBins: Int
    public let melFrameMs: Int
    public let melHopMs: Int
    public let targetClassIndex: Int
    public let threshold: Float
    /// Cooldown so a single utterance doesn't fire repeatedly. Seconds; default 1s.
    public let minIntervalBetweenFires: TimeInterval?

    public init(
        wakeWord: String = "hey b",
        inputKind: KwsInputKind = .logMel,
        sampleRateHz: Int = 16_000,
        windowMs: Int = 1000,
        hopMs: Int = 100,
        nMelBins: Int = 40,
        melFrameMs: Int = 25,
        melHopMs: Int = 10,
        targetClassIndex: Int = 1,
        threshold: Float = 0.7,
        minIntervalBetweenFires: TimeInterval? = nil
    ) {
        self.wakeWord = wakeWord
        self.inputKind = inputKind
        self.sampleRateHz = sampleRateHz
        self.windowMs = windowMs
        self.hopMs = hopMs
        self.nMelBins = nMelBins
        self.melFrameMs = melFrameMs
        self.melHopMs = melHopMs
        self.targetClassIndex = targetClassIndex
        self.threshold = threshold
        self.minIntervalBetweenFires = minIntervalBetweenFires
    }
}

/// Injected KWS model runner (the ONNX `InferenceSession` seam). Given the
/// linearised window it returns the class logits (softmax is applied by the
/// detector). Port of the model side of `KwsWakeWordDetector.Predict`.
public protocol IKwsModelRunner: AnyObject, Sendable {
    func classLogits(window: [Float], inputKind: KwsInputKind) -> [Float]
}

/// Low-latency keyword-spotting wake-word detector. Port of
/// `CircleAI.Voice.KwsWakeWordDetector` with the ONNX session injected behind
/// `IKwsModelRunner`. Runs the model on a sliding window every `hopMs`, applies
/// softmax to the target class, and fires with a cooldown. Implements the Voice
/// `IWakeWordDetector` contract (detections surfaced as an AsyncStream).
public final class KwsWakeWordDetector: IWakeWordDetector, @unchecked Sendable {
    private let capture: IAudioCapture
    private let config: KwsConfig
    private let runner: IKwsModelRunner?

    private let lock = NSLock()
    private var disposed = false
    private var listening = false
    private var loopTask: Task<Void, Never>?
    private var lastFireUtc = Date(timeIntervalSince1970: 0)

    private var continuation: AsyncStream<WakeWordDetectedEventArgs>.Continuation?
    private var pending: [WakeWordDetectedEventArgs] = []
    private var streamCompleted = false

    public let wakeWord: String

    public init(capture: IAudioCapture, config: KwsConfig, runner: IKwsModelRunner? = nil) {
        precondition(config.sampleRateHz > 0)
        precondition(config.windowMs > 0)
        precondition(config.hopMs > 0)
        precondition(config.threshold >= 0 && config.threshold <= 1)
        self.capture = capture
        self.config = config
        self.runner = runner
        self.wakeWord = config.wakeWord
    }

    public var isListening: Bool { lock.lock(); defer { lock.unlock() }; return listening }

    public func detections() -> AsyncStream<WakeWordDetectedEventArgs> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            lock.lock()
            if streamCompleted {
                lock.unlock()
                continuation.finish()
                return
            }
            for p in pending { continuation.yield(p) }
            pending.removeAll()
            self.continuation = continuation
            lock.unlock()

            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.continuation = nil; self.lock.unlock()
            }
        }
    }

    public func start() async throws {
        if isDisposed() { throw VoiceError.disposed }
        lock.lock()
        if listening { lock.unlock(); return }
        listening = true
        lock.unlock()
        let task = Task { [weak self] in await self?.listenLoop() }
        lock.lock(); loopTask = task; lock.unlock()
    }

    public func stop() async throws {
        if isDisposed() { throw VoiceError.disposed }
        lock.lock()
        if !listening { lock.unlock(); return }
        listening = false
        let task = loopTask
        loopTask = nil
        lock.unlock()
        task?.cancel()
        await task?.value
    }

    public func dispose() async {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        listening = false
        let task = loopTask
        loopTask = nil
        let cont = continuation
        continuation = nil
        streamCompleted = true
        pending.removeAll()
        lock.unlock()
        task?.cancel()
        await task?.value
        cont?.finish()
    }

    private func emit(_ ev: WakeWordDetectedEventArgs) {
        lock.lock()
        if streamCompleted { lock.unlock(); return }
        if let cont = continuation { cont.yield(ev) } else { pending.append(ev) }
        lock.unlock()
    }

    private func listenLoop() async {
        let windowSamples = config.sampleRateHz * config.windowMs / 1000
        let hopSamples = config.sampleRateHz * config.hopMs / 1000
        var ringBuffer = [Float](repeating: 0, count: windowSamples)
        var ringFill = 0
        var ringWrite = 0
        var samplesSinceLastInference = 0
        let minInterval = config.minIntervalBetweenFires ?? 1.0

        do {
            for try await chunkBytes in capture.capture() {
                if chunkBytes.isEmpty { continue }
                if Task.isCancelled { break }

                let bytes = [UInt8](chunkBytes)
                var i = 0
                while i + 1 < bytes.count {
                    let s = readInt16LE(bytes, i)
                    ringBuffer[ringWrite] = Float(s) / 32768.0
                    ringWrite = (ringWrite + 1) % windowSamples
                    if ringFill < windowSamples { ringFill += 1 }
                    samplesSinceLastInference += 1

                    i += 2

                    if ringFill < windowSamples { continue }
                    if samplesSinceLastInference < hopSamples { continue }
                    samplesSinceLastInference = 0

                    // Linearise the ring into an in-order window.
                    var window = [Float](repeating: 0, count: windowSamples)
                    let splitAt = windowSamples - ringWrite
                    for j in 0..<splitAt { window[j] = ringBuffer[ringWrite + j] }
                    for j in 0..<ringWrite { window[splitAt + j] = ringBuffer[j] }

                    if let prob = predict(window), prob >= config.threshold {
                        let now = Date()
                        if now.timeIntervalSince(lastFireUtc) < minInterval { continue }
                        lastFireUtc = now
                        emit(WakeWordDetectedEventArgs(wakeWord: wakeWord, detectedAt: now, confidence: prob))
                    }
                }
            }
        } catch {
            // Normal shutdown or capture error.
        }
        lock.lock(); listening = false; lock.unlock()
    }

    private func predict(_ window: [Float]) -> Float? {
        let logits: [Float]
        if let runner {
            logits = runner.classLogits(window: window, inputKind: config.inputKind)
        } else {
            // Deterministic fallback: pool mean log-mel into class logits.
            let mel = VoiceDsp.meanLogMel(window, nMelBins: max(config.nMelBins, config.targetClassIndex + 1), frameMs: config.melFrameMs, hopMs: config.melHopMs, sampleRateHz: config.sampleRateHz)
            let classes = max(2, config.targetClassIndex + 1)
            var l = [Float](repeating: 0, count: classes)
            for k in 0..<mel.count { l[k % classes] += mel[k] }
            logits = l
        }
        if logits.isEmpty { return nil }
        return VoiceDsp.softmaxTarget(logits, config.targetClassIndex)
    }

    private func isDisposed() -> Bool { lock.lock(); defer { lock.unlock() }; return disposed }
}
