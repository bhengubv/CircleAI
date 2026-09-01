// VoiceLoop.swift
//
// The full hands-free conversation, assembled:
//
//   wake word -> VAD -> ASR -> BRAIN -> TTS -> audio out -> back to listening
//
// VoicePipeline already composed the EARS (wake -> VAD -> ASR) and raised a
// transcribed event. Nothing ever joined that to a brain or a mouth, so the
// hands-free loop did not exist end to end anywhere in the codebase — each half
// worked in isolation and no code closed the circle.
//
// The brain is a CLOSURE, not an AI service: the voice layer must not depend on
// the hosting layer (hosting depends on the speech contracts, not the reverse).
// The host supplies `text -> reply`, which is trivially a chat call.
//
// Ported from src/CircleAI.Voice/VoiceLoop.cs.

import Foundation

public final class VoiceLoop: @unchecked Sendable {

    public typealias Brain = @Sendable (String) async throws -> String

    private let ears: VoicePipeline
    private let brain: Brain
    private let mouth: any ITtsEngine
    private let speaker: any IAudioPlayer

    private let lock = NSLock()
    private var running = false
    private var consumer: Task<Void, Never>?
    private var speaking: Task<Void, Never>?
    private var bargeIn: Task<Void, Never>?
    private var queue: [TranscriptionResult] = []
    private var waiter: CheckedContinuation<Void, Never>?
    private var finished = false
    private var disposed = false

    /// Whether hearing the wake word while the assistant is talking stops it.
    public let allowBargeIn: Bool

    /// The assistant was interrupted mid-reply.
    public var onBargedIn: (@Sendable () -> Void)?

    /// One complete turn finished: what was heard and what was said back.
    public var onExchanged: (@Sendable (VoiceExchange) -> Void)?

    /// A turn failed. Surfaced rather than thrown, because the loop carries on.
    public var onFaulted: (@Sendable (Error) -> Void)?

    public init(ears: VoicePipeline,
                brain: @escaping Brain,
                mouth: any ITtsEngine,
                speaker: (any IAudioPlayer)? = nil,
                allowBargeIn: Bool = true) {
        self.ears = ears
        self.brain = brain
        self.mouth = mouth
        self.speaker = speaker ?? NullAudioPlayer()
        self.allowBargeIn = allowBargeIn
    }

    public func start() async throws {
        if isDisposed() { throw VoiceError.disposed }
        guard claimStart() else { return }

        // The pipeline is CALLBACK-based, not an async stream. Each activation
        // goes onto a queue drained by ONE consumer, so turns are processed one
        // at a time: a callback cannot await the brain, and letting turns overlap
        // would interleave two replies through one speaker.
        ears.onTranscribed = { [weak self] e in self?.enqueue(e.result) }

        // Barge-in listens on its OWN subscription to the wake stream. The
        // pipeline is already consuming one for activations, and an AsyncStream
        // has a single consumer - sharing it would mean every wake goes to
        // whichever of the two happened to be waiting.
        if allowBargeIn {
            let stream = ears.wakeDetector.detections()
            let task: Task<Void, Never> = Task { [weak self] in
                for await _ in stream {
                    if Task.isCancelled { break }
                    self?.interruptSpeech()
                }
            }
            setBargeIn(task)
        }

        consumer = Task { [weak self] in
            guard let self else { return }
            await self.consume()
        }

        try await ears.start()
    }

    public func stop() async {
        guard releaseStart() else { return }

        ears.onTranscribed = nil
        takeBargeIn()?.cancel()

        finish()
        cancelSpeech()

        try? await ears.stop()

        let c = takeConsumer()
        c?.cancel()
        await c?.value
    }

    public func close() async {
        if markDisposed() { return }
        await stop()
    }

    // MARK: - The turn

    private func consume() async {
        while let heard = await next() {
            if Task.isCancelled { return }
            guard !heard.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                continue
            }

            do {
                let reply = try await brain(heard.text)

                if !reply.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    let audio = try await mouth.synthesise(text: reply)
                    if !audio.audioData.isEmpty {
                        await speak(audio)
                    }
                }

                onExchanged?(VoiceExchange(heard: heard.text, replied: reply))
            } catch is CancellationError {
                return
            } catch {
                // A failed turn (model hiccup, TTS fault) must NOT kill the loop
                // — going permanently deaf is far worse than dropping one reply.
                onFaulted?(error)
            }
        }
    }

    /// Playback runs in its own task so a barge-in cancels ONLY the speaking, not
    /// the loop. Cancelling the loop here would make interrupting the assistant
    /// also switch it off, which is the opposite of what the person wanted.
    private func speak(_ audio: TtsSynthesisResult) async {
        let task: Task<Void, Never> = Task { [speaker] in
            do {
                try await speaker.play(pcm: audio.audioData,
                                       sampleRate: audio.sampleRate,
                                       channels: audio.channels,
                                       bitsPerSample: audio.bitsPerSample)
            } catch {
                // A cancelled play IS the barge-in, and a speaker that fails
                // must not take the loop down with it.
            }
        }
        setSpeaking(task)
        await task.value
        setSpeaking(nil)
    }

    private func interruptSpeech() {
        guard let task = takeSpeaking(), !task.isCancelled else { return }
        task.cancel()
        onBargedIn?()
    }

    // MARK: - The queue
    //
    // Hand-rolled rather than an AsyncStream so `stop()` can drain and end it
    // deterministically: a stream whose continuation is finished from another
    // task leaves the consumer's `for await` in a race with cancellation, and
    // the symptom of losing that race is a turn that plays after stop().

    private func enqueue(_ result: TranscriptionResult) {
        lock.lock()
        guard !finished else { lock.unlock(); return }
        queue.append(result)
        let w = waiter
        waiter = nil
        lock.unlock()
        w?.resume()
    }

    private func next() async -> TranscriptionResult? {
        while true {
            lock.lock()
            if !queue.isEmpty {
                let head = queue.removeFirst()
                lock.unlock()
                return head
            }
            if finished { lock.unlock(); return nil }

            await withCheckedContinuation { (c: CheckedContinuation<Void, Never>) in
                waiter = c
                lock.unlock()
            }
            if Task.isCancelled { return nil }
        }
    }

    private func finish() {
        lock.lock()
        finished = true
        let w = waiter
        waiter = nil
        lock.unlock()
        w?.resume()
    }

    // MARK: - Synchronous state
    //
    // Extracted because NSLock cannot be taken from an async context in Swift 6.

    private func claimStart() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if running { return false }
        running = true
        finished = false
        queue.removeAll()
        return true
    }

    private func releaseStart() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if !running { return false }
        running = false
        return true
    }

    private func isDisposed() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return disposed
    }

    private func markDisposed() -> Bool {
        lock.lock(); defer { lock.unlock() }
        if disposed { return true }
        disposed = true
        return false
    }

    private func setSpeaking(_ task: Task<Void, Never>?) {
        lock.lock(); speaking = task; lock.unlock()
    }

    private func takeSpeaking() -> Task<Void, Never>? {
        lock.lock(); defer { lock.unlock() }
        let t = speaking
        speaking = nil
        return t
    }

    private func cancelSpeech() {
        takeSpeaking()?.cancel()
    }

    private func setBargeIn(_ task: Task<Void, Never>?) {
        lock.lock(); bargeIn = task; lock.unlock()
    }

    private func takeBargeIn() -> Task<Void, Never>? {
        lock.lock(); defer { lock.unlock() }
        let t = bargeIn
        bargeIn = nil
        return t
    }

    private func takeConsumer() -> Task<Void, Never>? {
        lock.lock(); defer { lock.unlock() }
        let c = consumer
        consumer = nil
        return c
    }
}
