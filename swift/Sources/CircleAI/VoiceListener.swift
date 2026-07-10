// VoiceListener.swift
//
// Port of CircleAI.Companion.IVoiceListener + VoiceCompanionListener
// (IVoiceListener.cs, VoiceCompanionListener.cs).
//
// Bridges a voice pipeline with an ICompanionSession: when the wake word fires
// and the user speaks, the pipeline transcribes → the listener forwards the text
// to the session → the listener surfaces the Companion's reply. Platform hosts
// subscribe to drive TTS playback or UI updates.
//
// The Companion call is dispatched on a detached Task (fire-and-forget) so the
// wake-word detection path is never blocked. Session failures are logged and
// do not crash the host — consistent with the reference.
//
// C# uses events + a concrete VoicePipeline. Swift has no events, so the two
// notifications are exposed as settable callback closures, and the pipeline is
// modelled behind the IVoicePipeline protocol (the transcription source) so the
// listener runs + tests in-memory. `TranscriptionResult` is ported faithfully.

import Foundation

// =====================================================================
// Voice pipeline abstraction (the transcription source)
// =====================================================================

/// The final transcription for one wake-word activation. Faithful port of
/// `CircleAI.Voice.TranscriptionResult`.
public struct TranscriptionResult: Sendable, Equatable {
    public let text: String
    public let confidence: Float
    public let languageCode: String
    public init(text: String, confidence: Float, languageCode: String) {
        self.text = text
        self.confidence = confidence
        self.languageCode = languageCode
    }
}

/// A completed transcription produced after a wake-word activation. Port of
/// `CircleAI.Voice.TranscribedEventArgs`.
public struct TranscribedEvent: Sendable, Equatable {
    public let result: TranscriptionResult
    public let completedAt: Date
    public init(result: TranscriptionResult, completedAt: Date = Date()) {
        self.result = result
        self.completedAt = completedAt
    }
}

/// The voice pipeline the listener wires to — the transcription source. Modelled
/// (behind a protocol) on `CircleAI.Voice.VoicePipeline`: `start` begins
/// listening for the wake word; `stop` halts it; `transcribed` yields the final
/// `TranscribedEvent` for each activation. Hosts inject a real (Whisper-backed)
/// pipeline; tests inject an in-memory one.
public protocol IVoicePipeline: AnyObject, Sendable {
    /// Stream of completed transcriptions, one per wake-word activation.
    func transcribed() -> AsyncStream<TranscribedEvent>
    func start() async throws
    func stop() async throws
}

// =====================================================================
// Event args (surfaced by the listener)
// =====================================================================

/// Raised when a user utterance has been transcribed and is being forwarded to
/// the Companion session. Port of `UtteranceDetectedEventArgs`.
public struct UtteranceDetectedEvent: Sendable, Equatable {
    /// Transcribed text of the user's utterance.
    public let text: String
    /// Transcription confidence in [0, 1].
    public let confidence: Float
    /// UTC time the transcription completed.
    public let detectedAt: Date
    public init(text: String, confidence: Float, detectedAt: Date = Date()) {
        self.text = text
        self.confidence = confidence
        self.detectedAt = detectedAt
    }
}

/// Raised when the Companion has produced a reply to a voice utterance. The host
/// synthesises it to speech or shows it in the UI. Port of
/// `ResponseReadyEventArgs`.
public struct ResponseReadyEvent: Sendable, Equatable {
    /// The Companion's reply text.
    public let text: String
    /// The utterance that triggered this response.
    public let originalUtterance: String
    /// UTC time the Companion completed the reply.
    public let completedAt: Date
    public init(text: String, originalUtterance: String, completedAt: Date = Date()) {
        self.text = text
        self.originalUtterance = originalUtterance
        self.completedAt = completedAt
    }
}

// =====================================================================
// IVoiceListener
// =====================================================================

/// Bridges a voice pipeline with an `ICompanionSession`: listens for transcribed
/// utterances, forwards them to the session, and surfaces the Companion's reply.
/// Ported from `IVoiceListener`. The two C# events are exposed as callbacks:
/// set `onUtteranceDetected` / `onResponseReady`.
public protocol IVoiceListener: AnyObject {
    /// Invoked when a user utterance has been transcribed and is being forwarded.
    var onUtteranceDetected: (@Sendable (UtteranceDetectedEvent) -> Void)? { get set }
    /// Invoked when the Companion has produced a reply. Drive TTS / UI from here.
    var onResponseReady: (@Sendable (ResponseReadyEvent) -> Void)? { get set }
    /// Begin listening for the wake word. Starts the underlying pipeline.
    func start() async throws
    /// Stop listening and cancel any in-flight activation. Stops the pipeline.
    func stop() async throws
    /// Tear down: unsubscribe from the pipeline and release collaborators.
    func dispose() async
}

// =====================================================================
// VoiceCompanionListener
// =====================================================================

/// Concrete `IVoiceListener` wiring an `IVoicePipeline` to an `ICompanionSession`.
/// On each completed transcription it fires `onUtteranceDetected`, forwards the
/// text to the session on a detached Task, and fires `onResponseReady` with the
/// reply. Ported from `VoiceCompanionListener`. Session failures are logged and
/// swallowed. Disposing owns the pipeline's lifetime (its stream is drained /
/// cancelled).
public final class VoiceCompanionListener: IVoiceListener, @unchecked Sendable {
    private let pipeline: IVoicePipeline
    private let session: ICompanionSession

    private let lock = NSLock()
    private var disposed = false
    private var pumpTask: Task<Void, Never>?

    public var onUtteranceDetected: (@Sendable (UtteranceDetectedEvent) -> Void)?
    public var onResponseReady: (@Sendable (ResponseReadyEvent) -> Void)?

    public init(pipeline: IVoicePipeline, session: ICompanionSession) {
        self.pipeline = pipeline
        self.session = session
        // Subscribe to the pipeline's transcription stream (mirrors the C#
        // `_pipeline.Transcribed += OnTranscribed` in the constructor).
        startPump()
    }

    private func startPump() {
        let stream = pipeline.transcribed()
        pumpTask = Task { [weak self] in
            for await ev in stream {
                if Task.isCancelled { break }
                self?.onTranscribed(ev)
            }
        }
    }

    public func start() async throws {
        if isDisposed() { throw VoiceListenerError.disposed }
        try await pipeline.start()
    }

    public func stop() async throws {
        if isDisposed() { throw VoiceListenerError.disposed }
        try await pipeline.stop()
    }

    private func onTranscribed(_ e: TranscribedEvent) {
        if isDisposed() { return }

        let text = e.result.text
        let confidence = e.result.confidence
        let detectedAt = e.completedAt

        // Notify subscribers that we received an utterance.
        onUtteranceDetected?(UtteranceDetectedEvent(text: text, confidence: confidence, detectedAt: detectedAt))

        // Forward to the Companion asynchronously — never block the pump.
        Task { [weak self] in
            guard let self else { return }
            do {
                let reply = try await self.session.send(text)
                if !self.isDisposed() {
                    self.onResponseReady?(ResponseReadyEvent(
                        text: reply, originalUtterance: text, completedAt: Date()))
                }
            } catch {
                // Consistent with VoicePipeline.ActivationFailed semantics: log, don't crash.
                FileHandle.standardError.write(Data(
                    "VoiceCompanionListener: session failed for utterance '\(text)': \(error)\n".utf8))
            }
        }
    }

    public func dispose() async {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        let pump = pumpTask
        pumpTask = nil
        lock.unlock()

        pump?.cancel()
        // Owns the pipeline's lifetime — stop it on teardown.
        try? await pipeline.stop()
    }

    private func isDisposed() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return disposed
    }
}

/// Errors from a voice listener. Mirrors the C# `ObjectDisposedException` guard.
public enum VoiceListenerError: Error, Equatable {
    case disposed
}
