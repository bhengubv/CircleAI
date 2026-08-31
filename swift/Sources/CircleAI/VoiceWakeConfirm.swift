// VoiceWakeConfirm.swift
//
// A keyword spotter fires on sound that resembles the phrase. A CONFIRMER
// decides whether it was actually somebody addressing the device, or the phrase
// appearing in the middle of a sentence aimed at another person.
//
// Ported from src/CircleAI.Voice/ConfirmedKeywordSpotter.cs.

import Foundation

/// One detection, with the audio around it.
public struct KwsDetection: Sendable, Equatable {
    /// A Zipformer frame is 40 ms. Everything in milliseconds derives from this.
    public static let msPerFrame = 40.0

    public let phrase: String
    public let atFrame: Int
    public let probability: Double
    /// -1 when the spotter did not report a start; the end is used instead.
    public let startFrame: Int

    public init(phrase: String, atFrame: Int, probability: Double, startFrame: Int = -1) {
        self.phrase = phrase
        self.atFrame = atFrame
        self.probability = probability
        self.startFrame = startFrame
    }

    public var startMs: Double { Double(startFrame < 0 ? atFrame : startFrame) * Self.msPerFrame }
    public var endMs: Double { Double(atFrame) * Self.msPerFrame }
}

public struct WakeCandidate: Sendable, Equatable {
    public let detection: KwsDetection
    /// The audio window the detection sits in, 16 kHz mono float.
    public let window: [Float]
    public let keywordStart: Int
    public let keywordEnd: Int

    public init(detection: KwsDetection, window: [Float], keywordStart: Int, keywordEnd: Int) {
        self.detection = detection
        self.window = window
        self.keywordStart = keywordStart
        self.keywordEnd = keywordEnd
    }
}

/// Confirms or rejects a candidate, and says why when it rejects.
public protocol IWakeConfirmer: AnyObject, Sendable {
    func confirm(_ candidate: WakeCandidate) async -> Bool
    var lastReason: String? { get }
}

/// Confirms everything. Useful when the spotter is already strict enough.
public final class AlwaysConfirm: IWakeConfirmer, @unchecked Sendable {
    public init() {}
    public var lastReason: String? { nil }
    public func confirm(_ candidate: WakeCandidate) async -> Bool { true }
}

/// Rejects a phrase that arrived in the MIDDLE of an utterance.
///
/// Somebody addressing a device pauses first. The same words inside a running
/// sentence are almost always about the device, not to it - which is why the
/// measurement here is how long the person had already been talking, not how
/// confident the acoustic model was.
public final class UtteranceOnsetConfirmer: IWakeConfirmer, @unchecked Sendable {
    /// Longer than this much continuous speech before the phrase ended and it
    /// was not an address.
    public var maxLeadInMs: Double = 600
    /// A quiet stretch shorter than this does not count as a pause.
    public var gapToleranceMs: Double = 150
    /// Speech is anything above this fraction of the window peak.
    public var speechFloor: Double = 0.12

    static let bucketMs = 10
    static let sampleRate = 16_000

    private let lock = NSLock()
    private var reason: String?

    public init(maxLeadInMs: Double = 600, gapToleranceMs: Double = 150, speechFloor: Double = 0.12) {
        self.maxLeadInMs = maxLeadInMs
        self.gapToleranceMs = gapToleranceMs
        self.speechFloor = speechFloor
    }

    public var lastReason: String? {
        lock.lock(); defer { lock.unlock() }
        return reason
    }

    private func setReason(_ r: String?) { lock.lock(); reason = r; lock.unlock() }

    public func confirm(_ candidate: WakeCandidate) async -> Bool {
        let w = candidate.window
        // Nothing to judge means FAIL OPEN. A confirmer that rejects when it
        // cannot see is a device that stops answering.
        if w.isEmpty { setReason(nil); return true }

        let per = Self.bucketMs * (Self.sampleRate / 1000)   // samples per 10 ms bucket
        let n = w.count / per
        if n < 4 { setReason(nil); return true }

        var rms = [Float](repeating: 0, count: n)
        var peak: Float = 0
        for b in 0..<n {
            var s = 0.0
            for i in (b * per)..<((b + 1) * per) { s += Double(w[i]) * Double(w[i]) }
            rms[b] = Float((s / Double(per)).squareRoot())
            if rms[b] > peak { peak = rms[b] }
        }

        if peak <= 1e-6 { setReason("silence"); return false }

        let floor = peak * Float(speechFloor)
        let gap = max(1, Int(gapToleranceMs / Double(Self.bucketMs)))
        let endBucket = min(max(candidate.keywordEnd / per, 0), n - 1)

        // Walk BACKWARDS from the phrase end to find where the speech began.
        var onset = endBucket
        var quiet = 0
        var b = endBucket
        while b >= 0 {
            if rms[b] >= floor { onset = b; quiet = 0 }
            else { quiet += 1; if quiet >= gap { break } }
            b -= 1
        }

        let leadIn = Double((endBucket - onset + 1) * Self.bucketMs)
        if leadIn <= maxLeadInMs { setReason(nil); return true }

        setReason("had been speaking \(Int(leadIn)) ms before the phrase ended (max \(Int(maxLeadInMs)))")
        return false
    }
}

/// Re-transcribes the window and checks the phrase is how the utterance
/// STARTS, allowing a few filler words in front of it.
public final class TranscriptConfirmer: IWakeConfirmer, @unchecked Sendable {
    /// Fillers a person really does say before addressing a device.
    public var allowedLeadIn: Set<String> = [
        "um", "uh", "er", "erm", "ah", "oh", "hey", "ok", "okay", "so", "please", "yeah",
    ]
    public var timeout: TimeInterval = 0.7

    private let transcribe: @Sendable ([UInt8]) async throws -> String
    private let normalise: @Sendable (String) -> String
    private let lock = NSLock()
    private var reason: String?

    public init(transcribe: @escaping @Sendable ([UInt8]) async throws -> String,
                normalise: (@Sendable (String) -> String)? = nil) {
        self.transcribe = transcribe
        // Everything that is not a letter or a digit becomes a space, so
        // punctuation and casing cannot make a match fail.
        self.normalise = normalise ?? { s in
            String(s.lowercased().map { $0.isLetter || $0.isNumber ? $0 : " " })
        }
    }

    public var lastReason: String? {
        lock.lock(); defer { lock.unlock() }
        return reason
    }

    private func setReason(_ r: String?) { lock.lock(); reason = r; lock.unlock() }

    /// PCM-16 little-endian, which is what every transcriber here takes.
    public static func toPcm16(_ samples: [Float]) -> [UInt8] {
        var out = [UInt8](repeating: 0, count: samples.count * 2)
        for i in 0..<samples.count {
            let v = Int16(max(-32768, min(32767, (samples[i] * 32767).rounded())))
            let u = UInt16(bitPattern: v)
            out[i * 2] = UInt8(u & 0xFF)
            out[i * 2 + 1] = UInt8((u >> 8) & 0xFF)
        }
        return out
    }

    public func confirm(_ candidate: WakeCandidate) async -> Bool {
        do {
            let text = try await transcribe(Self.toPcm16(candidate.window))

            let heard = normalise(text).split(separator: " ", omittingEmptySubsequences: true).map(String.init)
            let phrase = normalise(candidate.detection.phrase)
                .split(separator: " ", omittingEmptySubsequences: true).map(String.init)

            // Nothing to judge - fail open rather than refusing to wake.
            if heard.isEmpty || phrase.isEmpty { setReason(nil); return true }

            var at = 0
            while at < heard.count && allowedLeadIn.contains(heard[at]) { at += 1 }

            if at + phrase.count <= heard.count {
                var match = true
                for j in 0..<phrase.count where heard[at + j] != phrase[j] { match = false; break }
                if match { setReason(nil); return true }
            }

            setReason("heard \(heard.prefix(6).joined(separator: " ")) - phrase is not how it starts")
            return false
        } catch {
            // A confirmer that is unavailable must not silence the device.
            setReason("confirmer unavailable (\(type(of: error))) - allowed")
            return true
        }
    }
}

/// Two confirmers in series: the CHEAP one first, then the PRECISE one.
///
/// Both must agree. The name says either, but the C# requires both, and this
/// port matches it - the cheap check exists to avoid paying for the expensive
/// one on the many candidates it can reject outright.
public final class EitherConfirmer: IWakeConfirmer, @unchecked Sendable {
    private let cheap: any IWakeConfirmer
    private let precise: any IWakeConfirmer
    private let lock = NSLock()
    private var reason: String?

    public init(_ cheap: any IWakeConfirmer, _ precise: any IWakeConfirmer) {
        self.cheap = cheap
        self.precise = precise
    }

    public var lastReason: String? {
        lock.lock(); defer { lock.unlock() }
        return reason
    }

    private func setReason(_ r: String?) { lock.lock(); reason = r; lock.unlock() }

    public func confirm(_ candidate: WakeCandidate) async -> Bool {
        if await !cheap.confirm(candidate) {
            setReason(cheap.lastReason)
            return false
        }
        if await !precise.confirm(candidate) {
            setReason(precise.lastReason)
            return false
        }
        setReason(nil)
        return true
    }
}
