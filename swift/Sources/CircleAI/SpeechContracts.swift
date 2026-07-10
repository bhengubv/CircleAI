// SpeechContracts.swift
//
// Port of CircleAI.Speech contract surface (Contracts.cs) + fail-closed
// null defaults (NullImplementations.cs).
//
// ASR / TTS / wake-word / OCR — every primitive needed for B! Butler's
// voice loop. Real backends are injected dependencies.
//
// NAMING: Swift flattens C# namespaces into a single `CircleAI` module.
// `CircleAI.Voice` already contributes a `TranscriptionResult`
// (text/confidence/languageCode) via VoiceListener.swift, so the
// `CircleAI.Speech` DTOs that would collide are prefixed `Speech*`:
//   TranscribedSegment  -> SpeechTranscribedSegment
//   TranscriptionResult -> SpeechTranscriptionResult
//   SynthesisResult     -> SpeechSynthesisResult
// and the Speech `IWakeWordDetector` becomes `ISpeechWakeWordDetector`
// (the Voice one keeps the plain name). OcrResult / OcrTextBlock /
// WakeWordEvent are unique and keep their names.

import Foundation

// =====================================================================
// DTOs
// =====================================================================

/// One transcribed segment. Port of `CircleAI.Speech.TranscribedSegment`.
public struct SpeechTranscribedSegment: Sendable, Equatable, Codable {
    public let text: String
    public let offset: TimeInterval
    public let duration: TimeInterval
    public let language: String?
    public let confidence: Float

    public init(
        text: String,
        offset: TimeInterval,
        duration: TimeInterval,
        language: String? = nil,
        confidence: Float = 0
    ) {
        self.text = text
        self.offset = offset
        self.duration = duration
        self.language = language
        self.confidence = confidence
    }
}

/// Outcome of one ASR call. Port of `CircleAI.Speech.TranscriptionResult`.
public struct SpeechTranscriptionResult: Sendable, Equatable, Codable {
    public let text: String
    public let language: String?
    public let segments: [SpeechTranscribedSegment]
    public let totalDuration: TimeInterval

    public init(
        text: String,
        language: String?,
        segments: [SpeechTranscribedSegment],
        totalDuration: TimeInterval
    ) {
        self.text = text
        self.language = language
        self.segments = segments
        self.totalDuration = totalDuration
    }
}

/// Outcome of one TTS call. Port of `CircleAI.Speech.SynthesisResult`.
/// `ReadOnlyMemory<byte>` maps to `Data`.
public struct SpeechSynthesisResult: Sendable, Equatable {
    public let audioPcm16Mono: Data
    public let sampleRateHz: Int
    public let duration: TimeInterval

    public init(audioPcm16Mono: Data, sampleRateHz: Int, duration: TimeInterval) {
        self.audioPcm16Mono = audioPcm16Mono
        self.sampleRateHz = sampleRateHz
        self.duration = duration
    }
}

/// One OCR result. Port of `CircleAI.Speech.OcrResult`.
public struct OcrResult: Sendable, Equatable, Codable {
    public let text: String
    public let blocks: [OcrTextBlock]

    public init(text: String, blocks: [OcrTextBlock]) {
        self.text = text
        self.blocks = blocks
    }
}

/// One detected text block in an OCR result. Port of `CircleAI.Speech.OcrTextBlock`.
public struct OcrTextBlock: Sendable, Equatable, Codable {
    public let text: String
    public let x: Int
    public let y: Int
    public let width: Int
    public let height: Int
    public let confidence: Float
    public let language: String?

    public init(
        text: String,
        x: Int,
        y: Int,
        width: Int,
        height: Int,
        confidence: Float,
        language: String? = nil
    ) {
        self.text = text
        self.x = x
        self.y = y
        self.width = width
        self.height = height
        self.confidence = confidence
        self.language = language
    }
}

/// One wake-word fire. Port of `CircleAI.Speech.WakeWordEvent`.
public struct WakeWordEvent: Sendable, Equatable {
    public let keyword: String
    public let confidence: Float
    public let detectedAtUtc: Date

    public init(keyword: String, confidence: Float, detectedAtUtc: Date) {
        self.keyword = keyword
        self.confidence = confidence
        self.detectedAtUtc = detectedAtUtc
    }
}

// =====================================================================
// Contracts
// =====================================================================

/// Convert audio to text. Port of `CircleAI.Speech.ISpeechRecognizer`.
public protocol ISpeechRecognizer: Sendable {
    /// Backend self-identification — "funasr-1.x" / "yapsnap" / "null".
    var backendId: String { get }

    /// Recognise one buffer of PCM-16 mono audio.
    func transcribe(
        audioPcm16Mono: Data,
        sampleRateHz: Int,
        languageHint: String?
    ) async throws -> SpeechTranscriptionResult
}

public extension ISpeechRecognizer {
    func transcribe(
        audioPcm16Mono: Data,
        sampleRateHz: Int
    ) async throws -> SpeechTranscriptionResult {
        try await transcribe(
            audioPcm16Mono: audioPcm16Mono,
            sampleRateHz: sampleRateHz,
            languageHint: nil)
    }
}

/// Convert text to spoken audio. Port of `CircleAI.Speech.ISpeechSynthesizer`.
public protocol ISpeechSynthesizer: Sendable {
    /// Backend self-identification — "chattts" / "null".
    var backendId: String { get }

    /// Synthesise one utterance. Returns PCM-16 mono.
    func synthesize(
        text: String,
        voiceId: String?,
        languageHint: String?
    ) async throws -> SpeechSynthesisResult
}

public extension ISpeechSynthesizer {
    func synthesize(text: String) async throws -> SpeechSynthesisResult {
        try await synthesize(text: text, voiceId: nil, languageHint: nil)
    }
}

/// Spot a wake word ("Hey B") in a continuous audio stream. Long-running
/// (`start`/`stop`). Port of `CircleAI.Speech.IWakeWordDetector` (renamed to
/// avoid the flat-module clash with `CircleAI.Voice.IWakeWordDetector`).
public protocol ISpeechWakeWordDetector: AnyObject, Sendable {
    /// Backend self-identification — "hey-snips" / "null".
    var backendId: String { get }

    /// Subscribe to wake-word fire events. Returns a token; call `dispose` on it
    /// to unsubscribe (mirrors the C# `IDisposable` handle).
    func subscribe(_ handler: @escaping @Sendable (WakeWordEvent) async -> Void) -> ISpeechSubscription

    /// Begin listening on the system mic. Idempotent.
    func start() async throws

    /// Stop listening. Idempotent.
    func stop() async throws

    /// Async dispose (mirrors `IAsyncDisposable`).
    func dispose() async
}

/// Handle returned by `ISpeechWakeWordDetector.subscribe`; disposing unsubscribes.
/// Port of the `IDisposable` the C# `Subscribe` returns.
public protocol ISpeechSubscription: AnyObject, Sendable {
    func dispose()
}

/// Acoustic echo canceller — subtracts the far-end reference from the near-end
/// mic input. Port of `CircleAI.Speech.IEchoCanceller`.
///
/// C# takes `ReadOnlySpan<byte>` / `Span<byte>`; Swift models the inputs as
/// `[UInt8]` (mic, far-end) and returns the freshly written destination bytes
/// plus the count. (Spans are not `Sendable`/storable in Swift; returning the
/// buffer preserves the same data flow deterministically.)
public protocol IEchoCanceller: AnyObject {
    /// Backend self-identification — "nlms" / "webrtc-aec3" / "null".
    var backendId: String { get }

    /// Cancel echo of `farEndReference` out of `nearEndMicrophone`. Both inputs
    /// must be the same sample rate and length (PCM-16 mono). Returns the
    /// cancelled PCM-16 mono bytes (same length as the near-end input).
    func cancel(
        nearEndMicrophone: [UInt8],
        farEndReference: [UInt8],
        sampleRateHz: Int
    ) -> [UInt8]

    /// Reset adaptive-filter state at the start of a new call.
    func reset()
}

/// Audio noise reducer — cleans a frame of PCM-16 mono audio. Port of
/// `CircleAI.Speech.INoiseReducer`.
public protocol INoiseReducer: AnyObject {
    /// Backend self-identification — "krisp" / "deepfilternet" / "passthrough" / "null".
    var backendId: String { get }

    /// True when the underlying model / runtime is available.
    var isAvailable: Bool { get }

    /// Reduce noise in `audioPcm16Mono`. Returns the cleaned PCM-16 mono bytes.
    func reduce(audioPcm16Mono: [UInt8], sampleRateHz: Int) -> [UInt8]
}

/// Verdict on whether a partial transcript represents a finished thought.
/// Port of `CircleAI.Speech.EndOfTurnResult`.
public struct EndOfTurnResult: Sendable, Equatable {
    /// True if the speaker likely finished their turn.
    public let isComplete: Bool
    /// 0..1 confidence.
    public let confidence: Float
    /// If `isComplete == false`, how many extra ms to wait before re-asking.
    public let waitMoreMs: Int

    public init(isComplete: Bool, confidence: Float, waitMoreMs: Int) {
        self.isComplete = isComplete
        self.confidence = confidence
        self.waitMoreMs = waitMoreMs
    }
}

/// Decide whether the caller has finished their turn. Port of
/// `CircleAI.Speech.IEndOfTurnDetector`.
public protocol IEndOfTurnDetector: AnyObject {
    /// Backend self-identification — "rules" / "smart-turn-v2" / "null".
    var backendId: String { get }

    /// Classify the current state.
    func predict(partialTranscript: String, trailingSilence: TimeInterval) -> EndOfTurnResult

    /// Reset internal state at the start of a fresh turn.
    func reset()
}

/// One verdict from a voice-activity detector. Port of
/// `CircleAI.Speech.VadFrameResult`.
public struct VadFrameResult: Sendable, Equatable {
    /// True if this frame contains speech.
    public let isSpeech: Bool
    /// 0..1 confidence the frame is speech.
    public let speechProbability: Float
    /// Frame start offset relative to the stream start.
    public let offset: TimeInterval

    public init(isSpeech: Bool, speechProbability: Float, offset: TimeInterval) {
        self.isSpeech = isSpeech
        self.speechProbability = speechProbability
        self.offset = offset
    }
}

/// Frame-level voice-activity detector. Port of the frame-based
/// `CircleAI.Speech.IVoiceActivityDetector` (renamed to avoid the flat-module
/// clash with the stream-based `CircleAI.Voice.IVoiceActivityDetector`).
public protocol IFrameVoiceActivityDetector: AnyObject {
    /// Backend self-identification — "energy" / "silero" / "null".
    var backendId: String { get }

    /// Speech probability threshold for `VadFrameResult.isSpeech`.
    var speechThreshold: Float { get }

    /// Classify one frame of PCM-16 mono audio.
    func classify(audioPcm16Mono: [UInt8], sampleRateHz: Int, offset: TimeInterval) -> VadFrameResult

    /// Reset any internal hangover state at the start of a fresh utterance.
    func reset()
}

/// Read text out of an image. Port of `CircleAI.Speech.IOpticalCharacterRecognizer`.
public protocol IOpticalCharacterRecognizer: Sendable {
    /// Backend self-identification — "paddleocr-2.x" / "null".
    var backendId: String { get }

    /// Recognise text in an image. `languageHint` e.g. "eng" / "chi" / "auto".
    func recognize(imageBytes: Data, languageHint: String?) async throws -> OcrResult
}

public extension IOpticalCharacterRecognizer {
    func recognize(imageBytes: Data) async throws -> OcrResult {
        try await recognize(imageBytes: imageBytes, languageHint: "auto")
    }
}

// =====================================================================
// Null implementations (fail-closed defaults) — NullImplementations.cs
// =====================================================================

/// Port of `CircleAI.Speech.NullSpeechRecognizer`.
public final class NullSpeechRecognizer: ISpeechRecognizer, @unchecked Sendable {
    public static let instance = NullSpeechRecognizer()
    public init() {}
    public var backendId: String { "null" }

    public func transcribe(
        audioPcm16Mono: Data,
        sampleRateHz: Int,
        languageHint: String?
    ) async throws -> SpeechTranscriptionResult {
        SpeechTranscriptionResult(
            text: "",
            language: languageHint,
            segments: [],
            totalDuration: 0)
    }
}

/// Port of `CircleAI.Speech.NullSpeechSynthesizer`.
public final class NullSpeechSynthesizer: ISpeechSynthesizer, @unchecked Sendable {
    public static let instance = NullSpeechSynthesizer()
    public init() {}
    public var backendId: String { "null" }

    public func synthesize(
        text: String,
        voiceId: String?,
        languageHint: String?
    ) async throws -> SpeechSynthesisResult {
        SpeechSynthesisResult(audioPcm16Mono: Data(), sampleRateHz: 16_000, duration: 0)
    }
}

/// Port of `CircleAI.Speech.NullWakeWordDetector`. Tracks nothing; never fires.
public final class NullSpeechWakeWordDetector: ISpeechWakeWordDetector, @unchecked Sendable {
    public init() {}
    public var backendId: String { "null" }

    public func subscribe(_ handler: @escaping @Sendable (WakeWordEvent) async -> Void) -> ISpeechSubscription {
        EmptySubscription.instance
    }
    public func start() async throws {}
    public func stop() async throws {}
    public func dispose() async {}

    /// Port of the private `EmptyDisposable`.
    public final class EmptySubscription: ISpeechSubscription, @unchecked Sendable {
        public static let instance = EmptySubscription()
        public func dispose() {}
    }
}

/// Port of `CircleAI.Speech.NullOpticalCharacterRecognizer`.
public final class NullOpticalCharacterRecognizer: IOpticalCharacterRecognizer, @unchecked Sendable {
    public static let instance = NullOpticalCharacterRecognizer()
    public init() {}
    public var backendId: String { "null" }

    public func recognize(imageBytes: Data, languageHint: String?) async throws -> OcrResult {
        OcrResult(text: "", blocks: [])
    }
}
