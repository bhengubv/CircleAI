// VoiceCore.swift
//
// Port of the CircleAI.Voice core contracts + deterministic implementations:
//   - AudioFormat.cs                 -> AudioFormat
//   - IAudioCapture (VoicePipeline.cs) + NullAudioCapture
//   - IVoiceActivityDetector.cs      -> IVoiceActivityDetector (stream) + VadSegment
//   - IVoiceTranscriber.cs           -> IVoiceTranscriber + PartialTranscription
//                                       (reuses TranscriptionResult from VoiceListener.swift)
//   - IWakeWordDetector.cs           -> IWakeWordDetector + WakeWordDetectedEventArgs
//   - ITtsEngine.cs                  -> ITtsEngine + TtsSynthesisResult
//   - NullVoiceTranscriber / NullWakeWordDetector / NullVoiceActivityDetector /
//     NullTtsEngine
//   - EnergyVadDetector.cs           -> EnergyVadDetector (RMS energy stream VAD)
//   - EnergyWakeWordDetector.cs      -> EnergyWakeWordDetector
//   - VoicePipeline.cs               -> VoicePipeline (reuses TranscribedEvent)
//
// C# events (WakeWordDetected / Transcribed / ActivationFailed) have no Swift
// analogue, so wake-word detections are surfaced as an AsyncStream the pipeline
// subscribes to, and the pipeline exposes callback closures for transcription /
// failure. Async byte streams (CaptureAsync / DetectAsync / StreamTranscribeAsync)
// map to AsyncThrowingStream<Data, Error>.
//
// CONCURRENCY: continuations are snapshotted under the NSLock and finish()ed
// OUTSIDE it (onTermination re-enters the lock); subscribers attach
// synchronously before the producer task is spawned; fan-out buffering is
// unbounded so a detection published right after start() is never lost.

import Foundation

// =====================================================================
// AudioFormat (AudioFormat.cs)
// =====================================================================

/// Describes a PCM audio format expected or produced by voice components. Port
/// of `CircleAI.Voice.AudioFormat`.
public struct AudioFormat: Sendable, Equatable, Codable {
    public let sampleRate: Int
    public let channels: Int
    public let bitsPerSample: Int

    public init(sampleRate: Int, channels: Int, bitsPerSample: Int) {
        self.sampleRate = sampleRate
        self.channels = channels
        self.bitsPerSample = bitsPerSample
    }

    /// Canonical input format expected by Butler / B! voice components: PCM
    /// signed 16-bit, mono, 16 kHz.
    public static let pcm16Mono16k = AudioFormat(sampleRate: 16_000, channels: 1, bitsPerSample: 16)
}

// =====================================================================
// IAudioCapture + NullAudioCapture (VoicePipeline.cs)
// =====================================================================

/// Captures raw audio from a platform input (microphone) and exposes it as an
/// asynchronous stream of PCM byte chunks. Port of `CircleAI.Voice.IAudioCapture`.
public protocol IAudioCapture: AnyObject, Sendable {
    /// The PCM format produced by `capture`.
    var format: AudioFormat { get }

    /// Begin capturing audio. The returned sequence yields PCM chunks until the
    /// task is cancelled or the underlying capture stops.
    func capture() -> AsyncThrowingStream<Data, Error>

    /// Async dispose (mirrors `IAsyncDisposable`).
    func dispose() async
}

/// No-op `IAudioCapture` that yields no audio. Port of `CircleAI.Voice.NullAudioCapture`.
public final class NullAudioCapture: IAudioCapture, @unchecked Sendable {
    public init() {}
    public let format: AudioFormat = .pcm16Mono16k

    public func capture() -> AsyncThrowingStream<Data, Error> {
        AsyncThrowingStream { continuation in
            continuation.finish()   // yield break
        }
    }

    public func dispose() async {}
}

// =====================================================================
// IVoiceActivityDetector (stream) + VadSegment (IVoiceActivityDetector.cs)
// =====================================================================

/// One segment identified by a stream VAD. Port of `CircleAI.Voice.VadSegment`.
public struct VadSegment: Sendable, Equatable {
    /// The raw PCM audio bytes for this segment. Non-empty for speech segments.
    public let audio: Data
    /// `true` when this segment contains detected speech.
    public let isSpeech: Bool

    public init(audio: Data, isSpeech: Bool) {
        self.audio = audio
        self.isSpeech = isSpeech
    }
}

/// Stream voice-activity detector. Processes an incoming audio stream and yields
/// only the segments that contain speech. Port of the stream-based
/// `CircleAI.Voice.IVoiceActivityDetector` (the frame-based Speech VAD is
/// `IFrameVoiceActivityDetector`).
public protocol IVoiceActivityDetector: AnyObject, Sendable {
    func detect(audioStream: AsyncThrowingStream<Data, Error>) -> AsyncThrowingStream<VadSegment, Error>
}

/// No-op stream VAD that passes all chunks through as speech. Port of
/// `CircleAI.Voice.NullVoiceActivityDetector`.
public final class NullVoiceActivityDetector: IVoiceActivityDetector, @unchecked Sendable {
    public init() {}

    public func detect(audioStream: AsyncThrowingStream<Data, Error>) -> AsyncThrowingStream<VadSegment, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    for try await chunk in audioStream {
                        continuation.yield(VadSegment(audio: chunk, isSpeech: true))
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}

// =====================================================================
// IVoiceTranscriber + PartialTranscription + NullVoiceTranscriber
// (IVoiceTranscriber.cs / NullVoiceTranscriber.cs)
// Note: TranscriptionResult(text, confidence, languageCode) is defined in
// VoiceListener.swift — reused here.
// =====================================================================

/// Partial or final transcription produced during streaming recognition. Port of
/// `CircleAI.Voice.PartialTranscription`.
public struct PartialTranscription: Sendable, Equatable {
    public let text: String
    public let isFinal: Bool
    public let confidence: Float

    public init(text: String, isFinal: Bool, confidence: Float) {
        self.text = text
        self.isFinal = isFinal
        self.confidence = confidence
    }
}

/// Converts captured audio into text. Port of `CircleAI.Voice.IVoiceTranscriber`.
public protocol IVoiceTranscriber: AnyObject, Sendable {
    /// Transcribe a complete audio buffer (PCM 16-bit, 16 kHz mono).
    func transcribe(pcmAudio: Data) async throws -> TranscriptionResult

    /// Stream audio chunks and receive partial transcriptions as produced. The
    /// final element has `isFinal == true`.
    func streamTranscribe(audioChunks: AsyncThrowingStream<Data, Error>) -> AsyncThrowingStream<PartialTranscription, Error>

    /// Async dispose (mirrors `IAsyncDisposable`).
    func dispose() async
}

/// No-op `IVoiceTranscriber`. Returns empty results without consuming audio.
/// Port of `CircleAI.Voice.NullVoiceTranscriber`.
public final class NullVoiceTranscriber: IVoiceTranscriber, @unchecked Sendable {
    private let lock = NSLock()
    private var disposed = false

    public init() {}

    public func transcribe(pcmAudio: Data) async throws -> TranscriptionResult {
        if isDisposed() { throw VoiceError.disposed }
        return TranscriptionResult(text: "", confidence: 0, languageCode: "und")
    }

    public func streamTranscribe(audioChunks: AsyncThrowingStream<Data, Error>) -> AsyncThrowingStream<PartialTranscription, Error> {
        AsyncThrowingStream { continuation in
            if self.isDisposed() {
                continuation.finish(throwing: VoiceError.disposed)
                return
            }
            let task = Task {
                do {
                    // Drain the input so producers aren't blocked, but emit nothing.
                    for try await _ in audioChunks { /* discard */ }
                    continuation.finish()   // yield break
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func dispose() async {
        lock.lock(); disposed = true; lock.unlock()
    }

    private func isDisposed() -> Bool {
        lock.lock(); defer { lock.unlock() }; return disposed
    }
}

// =====================================================================
// ITtsEngine + TtsSynthesisResult + NullTtsEngine (ITtsEngine.cs / NullTtsEngine.cs)
// =====================================================================

/// Result of a single-shot TTS synthesis operation. Port of
/// `CircleAI.Voice.TtsSynthesisResult`.
public struct TtsSynthesisResult: Sendable, Equatable {
    public let audioData: Data
    public let sampleRate: Int
    public let channels: Int
    public let bitsPerSample: Int

    public init(audioData: Data, sampleRate: Int, channels: Int, bitsPerSample: Int) {
        self.audioData = audioData
        self.sampleRate = sampleRate
        self.channels = channels
        self.bitsPerSample = bitsPerSample
    }
}

/// Text-to-speech engine that converts generated text into PCM audio. Port of
/// `CircleAI.Voice.ITtsEngine`.
public protocol ITtsEngine: AnyObject, Sendable {
    /// Synthesise `text` to a single PCM audio buffer.
    func synthesise(text: String) async throws -> TtsSynthesisResult

    /// Stream PCM audio chunks as they are synthesised.
    func streamSynthesise(text: String) -> AsyncThrowingStream<Data, Error>
}

/// No-op `ITtsEngine`. Returns empty audio and yields nothing. Port of
/// `CircleAI.Voice.NullTtsEngine`.
public final class NullTtsEngine: ITtsEngine, @unchecked Sendable {
    /// The PCM format a real engine would use: 24 kHz, mono, 16-bit.
    public static let emptyResult = TtsSynthesisResult(audioData: Data(), sampleRate: 24_000, channels: 1, bitsPerSample: 16)

    public init() {}

    public func synthesise(text: String) async throws -> TtsSynthesisResult {
        NullTtsEngine.emptyResult
    }

    public func streamSynthesise(text: String) -> AsyncThrowingStream<Data, Error> {
        AsyncThrowingStream { continuation in
            continuation.finish()   // yield break
        }
    }
}

// =====================================================================
// IWakeWordDetector + WakeWordDetectedEventArgs (IWakeWordDetector.cs)
// =====================================================================

/// Payload describing a single wake-word detection event. Port of
/// `CircleAI.Voice.WakeWordDetectedEventArgs`.
public struct WakeWordDetectedEventArgs: Sendable, Equatable {
    /// The wake word phrase that was detected.
    public let wakeWord: String
    /// UTC timestamp at which the detection fired.
    public let detectedAt: Date
    /// Detector-reported confidence in [0, 1].
    public let confidence: Float

    public init(wakeWord: String, detectedAt: Date = Date(), confidence: Float) {
        self.wakeWord = wakeWord
        self.detectedAt = detectedAt
        self.confidence = confidence
    }
}

/// Detects a configured wake word in a continuous audio stream. Port of
/// `CircleAI.Voice.IWakeWordDetector`. The C# `WakeWordDetected` event is
/// surfaced as the `detections()` AsyncStream.
public protocol IWakeWordDetector: AnyObject, Sendable {
    /// The phrase the detector listens for (e.g. "Hey B").
    var wakeWord: String { get }

    /// True when the detector is actively listening for the wake word.
    var isListening: Bool { get }

    /// Stream of wake-word detections (the C# `WakeWordDetected` event).
    func detections() -> AsyncStream<WakeWordDetectedEventArgs>

    /// Begin listening for the wake word. Idempotent.
    func start() async throws

    /// Stop listening and release capture resources. Idempotent.
    func stop() async throws

    /// Async dispose (mirrors `IAsyncDisposable`).
    func dispose() async
}

/// No-op `IWakeWordDetector`. Tracks listening state but never fires. Port of
/// `CircleAI.Voice.NullWakeWordDetector`.
public final class NullWakeWordDetector: IWakeWordDetector, @unchecked Sendable {
    private let lock = NSLock()
    private var disposed = false
    private var listening = false

    public let wakeWord: String

    /// Default Butler / B! wake word "Hey B".
    public convenience init() { self.init(wakeWord: "Hey B") }

    public init(wakeWord: String) {
        precondition(!wakeWord.trimmingCharacters(in: .whitespaces).isEmpty, "wakeWord required")
        self.wakeWord = wakeWord
    }

    public var isListening: Bool { lock.lock(); defer { lock.unlock() }; return listening }

    public func detections() -> AsyncStream<WakeWordDetectedEventArgs> {
        // Never raised — completes immediately.
        AsyncStream { $0.finish() }
    }

    public func start() async throws {
        if isDisposed() { throw VoiceError.disposed }
        lock.lock(); listening = true; lock.unlock()
    }

    public func stop() async throws {
        if isDisposed() { throw VoiceError.disposed }
        lock.lock(); listening = false; lock.unlock()
    }

    public func dispose() async {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        listening = false
        lock.unlock()
    }

    private func isDisposed() -> Bool { lock.lock(); defer { lock.unlock() }; return disposed }
}

/// Errors from the Voice module. Mirrors the C# `ObjectDisposedException` guards.
public enum VoiceError: Error, Equatable {
    case disposed
}

// =====================================================================
// EnergyVadDetector (EnergyVadDetector.cs) — RMS-energy stream VAD
// =====================================================================

/// Energy-based stream `IVoiceActivityDetector` using RMS energy to distinguish
/// speech from silence. Port of `CircleAI.Voice.EnergyVadDetector`. Buffers
/// speech frames until `silenceFrameCount` consecutive below-threshold frames,
/// then emits the buffered segment. Emits a trailing partial on stream end.
public final class EnergyVadDetector: IVoiceActivityDetector, @unchecked Sendable {
    public let energyThreshold: Float
    public let silenceFrameCount: Int
    public let frameSizeBytes: Int

    public init(energyThreshold: Float = 0.02, silenceFrames: Int = 15, frameSizeBytes: Int = 640) {
        precondition(silenceFrames > 0)
        precondition(frameSizeBytes > 0)
        precondition(energyThreshold >= 0)
        self.energyThreshold = energyThreshold
        self.silenceFrameCount = silenceFrames
        self.frameSizeBytes = frameSizeBytes
    }

    public func detect(audioStream: AsyncThrowingStream<Data, Error>) -> AsyncThrowingStream<VadSegment, Error> {
        AsyncThrowingStream { continuation in
            let task = Task { [energyThreshold, silenceFrameCount, frameSizeBytes] in
                var residual = [UInt8]()          // carry-over bytes not filling a frame
                var speechBuffer = [UInt8]()       // current speech segment accumulator
                var inSpeech = false
                var consecutiveSilenceFrames = 0

                do {
                    for try await chunk in audioStream {
                        if chunk.isEmpty { continue }
                        residual.append(contentsOf: chunk)

                        var offset = 0
                        while residual.count - offset >= frameSizeBytes {
                            let frame = Array(residual[offset..<(offset + frameSizeBytes)])
                            let rms = EnergyVadDetector.computeRmsEnergy(frame)
                            let isSpeechFrame = rms >= energyThreshold

                            if isSpeechFrame {
                                if !inSpeech {
                                    inSpeech = true
                                    consecutiveSilenceFrames = 0
                                    speechBuffer.removeAll(keepingCapacity: true)
                                } else {
                                    consecutiveSilenceFrames = 0
                                }
                                speechBuffer.append(contentsOf: frame)
                            } else if inSpeech {
                                // Buffer silence frames in case speech resumes.
                                speechBuffer.append(contentsOf: frame)
                                consecutiveSilenceFrames += 1
                                if consecutiveSilenceFrames >= silenceFrameCount {
                                    inSpeech = false
                                    consecutiveSilenceFrames = 0
                                    continuation.yield(VadSegment(audio: Data(speechBuffer), isSpeech: true))
                                    speechBuffer.removeAll(keepingCapacity: true)
                                }
                            }
                            // else: silence while not in speech — discard.

                            offset += frameSizeBytes
                        }

                        // Drop consumed bytes; keep the unconsumed remainder.
                        if offset > 0 {
                            residual.removeFirst(offset)
                        }
                    }

                    // Stream ended — if mid-speech, emit what we have.
                    if inSpeech && !speechBuffer.isEmpty {
                        continuation.yield(VadSegment(audio: Data(speechBuffer), isSpeech: true))
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// RMS energy of a PCM-16 frame, normalised to [0, 1]. Port of
    /// `EnergyVadDetector.ComputeRmsEnergy` (normalises by 32768.0).
    internal static func computeRmsEnergy(_ frameBytes: [UInt8]) -> Float {
        let sampleCount = frameBytes.count / 2
        if sampleCount == 0 { return 0 }
        var sumSquares: Double = 0
        for i in 0..<sampleCount {
            let normalised = Double(readInt16LE(frameBytes, i * 2)) / 32768.0
            sumSquares += normalised * normalised
        }
        return Float((sumSquares / Double(sampleCount)).squareRoot())
    }
}

// =====================================================================
// EnergyWakeWordDetector (EnergyWakeWordDetector.cs)
// =====================================================================

/// `IWakeWordDetector` that combines energy-based VAD with speech-to-text to
/// detect a configurable wake word. Port of `CircleAI.Voice.EnergyWakeWordDetector`.
/// Captures continuously, transcribes short speech segments, and fires a
/// detection when the transcription contains the wake word (case-insensitive).
public final class EnergyWakeWordDetector: IWakeWordDetector, @unchecked Sendable {
    private let capture: IAudioCapture
    private let transcriber: IVoiceTranscriber
    private let vad: EnergyVadDetector

    private let lock = NSLock()
    private var disposed = false
    private var listening = false
    private var listenTask: Task<Void, Never>?
    // Fan-out sink for detections (unbounded so a fire right after start isn't lost).
    private var continuation: AsyncStream<WakeWordDetectedEventArgs>.Continuation?
    private var pending: [WakeWordDetectedEventArgs] = []
    private var streamCompleted = false

    public let wakeWord: String

    public init(
        capture: IAudioCapture,
        transcriber: IVoiceTranscriber,
        wakeWord: String = "hey b",
        energyThreshold: Float = 0.02
    ) {
        precondition(!wakeWord.trimmingCharacters(in: .whitespaces).isEmpty, "wakeWord required")
        self.capture = capture
        self.transcriber = transcriber
        self.wakeWord = wakeWord.trimmingCharacters(in: .whitespaces)
        self.vad = EnergyVadDetector(energyThreshold: energyThreshold, silenceFrames: 10, frameSizeBytes: 640)
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

        let task = Task { [weak self] in
            await self?.listenLoop()
        }
        lock.lock(); listenTask = task; lock.unlock()
    }

    public func stop() async throws {
        if isDisposed() { throw VoiceError.disposed }
        lock.lock()
        if !listening { lock.unlock(); return }
        listening = false
        let task = listenTask
        listenTask = nil
        lock.unlock()

        task?.cancel()
        await task?.value
    }

    public func dispose() async {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        listening = false
        let task = listenTask
        listenTask = nil
        // Snapshot the continuation, release the lock, THEN finish (onTermination re-enters the lock).
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
        if let cont = continuation {
            cont.yield(ev)
        } else {
            pending.append(ev)
        }
        lock.unlock()
    }

    private func listenLoop() async {
        let audioStream = capture.capture()
        let segments = vad.detect(audioStream: audioStream)
        do {
            for try await segment in segments {
                if Task.isCancelled { break }
                if !segment.isSpeech || segment.audio.isEmpty { continue }

                let result: TranscriptionResult
                do {
                    result = try await transcriber.transcribe(pcmAudio: segment.audio)
                } catch is CancellationError {
                    break
                } catch {
                    // Transcription failed for this segment — skip and keep listening.
                    continue
                }

                if result.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { continue }

                if result.text.range(of: wakeWord, options: .caseInsensitive) != nil {
                    emit(WakeWordDetectedEventArgs(
                        wakeWord: wakeWord,
                        detectedAt: Date(),
                        confidence: result.confidence))
                }
            }
        } catch {
            // Capture/VAD stream error or cancellation — treat as normal shutdown.
        }
        lock.lock(); listening = false; lock.unlock()
    }

    private func isDisposed() -> Bool { lock.lock(); defer { lock.unlock() }; return disposed }
}

// =====================================================================
// VoicePipeline (VoicePipeline.cs) — reuses TranscribedEvent from VoiceListener.swift
// =====================================================================

/// Convenience composition of a wake-word detector, audio capture, transcriber,
/// and optional VAD + TTS. On wake-word detection it starts capturing audio,
/// optionally filters through VAD, feeds speech chunks to the transcriber, and
/// fires `onTranscribed` with the final `TranscriptionResult`. Port of
/// `CircleAI.Voice.VoicePipeline`.
///
/// The C# `Transcribed` / `ActivationFailed` events are exposed as settable
/// callback closures; disposing the pipeline disposes all collaborators.
public final class VoicePipeline: @unchecked Sendable {
    private let wake: IWakeWordDetector
    private let transcriber: IVoiceTranscriber
    private let capture: IAudioCapture
    private let vad: IVoiceActivityDetector?

    private let lock = NSLock()
    private var disposed = false
    private var activationTask: Task<Void, Never>?
    private var wakePump: Task<Void, Never>?

    /// Raised when a wake-word activation produces a final transcription.
    public var onTranscribed: (@Sendable (TranscribedEvent) -> Void)?
    /// Raised when an activation fails (capture / transcription / cancellation error).
    public var onActivationFailed: (@Sendable (Error) -> Void)?

    /// The optional TTS engine supplied at construction (host drives playback).
    public let ttsEngine: ITtsEngine?

    public var wakeDetector: IWakeWordDetector { wake }
    public var voiceTranscriber: IVoiceTranscriber { transcriber }
    public var audioCapture: IAudioCapture { capture }
    public var voiceActivityDetector: IVoiceActivityDetector? { vad }

    public init(
        wake: IWakeWordDetector,
        transcriber: IVoiceTranscriber,
        capture: IAudioCapture? = nil,
        vad: IVoiceActivityDetector? = nil,
        tts: ITtsEngine? = nil
    ) {
        self.wake = wake
        self.transcriber = transcriber
        self.capture = capture ?? NullAudioCapture()
        self.vad = vad
        self.ttsEngine = tts
        // Subscribe to the wake stream SYNCHRONOUSLY (mirrors the C# constructor's
        // `_wake.WakeWordDetected += OnWakeWordDetected`), then spawn the pump so
        // a detection fired right after start() is not lost.
        let stream = wake.detections()
        wakePump = Task { [weak self] in
            for await _ in stream {
                if Task.isCancelled { break }
                self?.onWakeWordDetected()
            }
        }
    }

    public func start() async throws {
        if isDisposed() { throw VoiceError.disposed }
        try await wake.start()
    }

    public func stop() async throws {
        if isDisposed() { throw VoiceError.disposed }
        cancelActivation()
        try await wake.stop()
    }

    private func onWakeWordDetected() {
        if isDisposed() { return }
        // Cancel any previous activation still running, then start a new one.
        cancelActivation()
        let task = Task { [weak self] in
            await self?.runActivation()
        }
        lock.lock(); activationTask = task; lock.unlock()
    }

    private func runActivation() async {
        do {
            // With VAD, pipe raw audio through it and pass only speech segments;
            // without VAD, forward the raw capture stream directly.
            let audioInput: AsyncThrowingStream<Data, Error>
            if let vad {
                audioInput = Self.extractSpeechSegments(vad: vad, rawAudio: capture.capture())
            } else {
                audioInput = capture.capture()
            }

            let result = try await Self.toFinal(transcriber.streamTranscribe(audioChunks: audioInput))
            if Task.isCancelled { return }
            if let result {
                onTranscribed?(TranscribedEvent(result: result))
            }
            // else: no final result — silence/noise/premature cancel; normal, no event.
        } catch is CancellationError {
            // Activation cancelled (stop requested or new wake event). Swallow.
        } catch {
            onActivationFailed?(error)
        }
    }

    /// Filter raw audio through the VAD and yield only speech-segment bytes. Port
    /// of `VoicePipeline.ExtractSpeechSegmentsAsync`.
    private static func extractSpeechSegments(
        vad: IVoiceActivityDetector,
        rawAudio: AsyncThrowingStream<Data, Error>
    ) -> AsyncThrowingStream<Data, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    for try await segment in vad.detect(audioStream: rawAudio) {
                        if segment.isSpeech {
                            continuation.yield(segment.audio)
                        }
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// Drain the partial-transcription stream and return the final result, or nil
    /// if the stream produces no items. Port of `ToFinalAsync`.
    private static func toFinal(_ source: AsyncThrowingStream<PartialTranscription, Error>) async throws -> TranscriptionResult? {
        var last: PartialTranscription?
        for try await partial in source {
            last = partial
            if partial.isFinal { break }
        }
        guard let last else { return nil }
        // Language is unknown at this layer; callers use single-shot transcribe for richer metadata.
        return TranscriptionResult(text: last.text, confidence: last.confidence, languageCode: "und")
    }

    private func cancelActivation() {
        lock.lock()
        let task = activationTask
        activationTask = nil
        lock.unlock()
        task?.cancel()
    }

    public func dispose() async {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        let pump = wakePump
        wakePump = nil
        let act = activationTask
        activationTask = nil
        lock.unlock()

        pump?.cancel()
        act?.cancel()
        await wake.dispose()
        await transcriber.dispose()
        await capture.dispose()
    }

    private func isDisposed() -> Bool { lock.lock(); defer { lock.unlock() }; return disposed }
}
