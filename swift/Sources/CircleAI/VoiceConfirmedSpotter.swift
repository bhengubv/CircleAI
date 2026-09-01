// VoiceConfirmedSpotter.swift
//
// Two stages, because one cannot be both cheap and certain.
//
// STAGE ONE is the zipformer, always on, deliberately generous. Measured on the
// P30 through a room, "Circle" was heard 12 times out of 12 — which is the
// number that matters, because a wake word that misses is a product that does
// not work.
//
// STAGE TWO is what stops that generosity becoming a nuisance. The same
// measurement over 30 clips of ordinary speech in three voices produced 21 false
// accepts, and EVERY SINGLE ONE was a sentence with the word inside it: "let us
// circle back", "draw a circle around the answer". None fired on speech that did
// not contain the word. The spotter was not wrong; people just say "circle".
//
// A THRESHOLD CANNOT FIX THIS AND IT IS WORTH SAYING WHY. "circle back" scores
// 0.802, higher than most genuine wakes. The two populations are not separated
// by confidence, so no cut through confidence divides them. They are separated
// by something else entirely, which is the whole idea: a wake word is the START
// of what you say. So stage two asks one question — was anyone talking just
// before this? — and that question costs no model, no memory and no measurable
// battery.
//
// Ported from src/CircleAI.Voice/ConfirmedKeywordSpotter.cs. The zipformer
// itself needs onnxruntime and stays behind `IKeywordSpotter`: the two-stage
// POLICY crosses, the graph does not.

import Foundation

/// Stage one: something that hears phrases in a stream of audio.
///
/// A protocol rather than the zipformer itself so the policy above can be built
/// and tested without onnxruntime, and so a different first stage can be dropped
/// in without touching any of it.
public protocol IKeywordSpotter: AnyObject {
    var keywords: [String] { get }

    /// Phrases that can never fire because another keyword is a prefix of them.
    /// Reported rather than silently dropped: somebody typed that phrase in and
    /// deserves to be told it will never work.
    var shadowedKeywords: [(phrase: String, shadowedBy: String)] { get }

    /// Called for each detection as it is decoded.
    var onDetected: ((KwsDetection) -> Void)? { get set }

    func acceptWaveform(_ samples: [Float])
    func flush()
    func reset()
}

/// Wraps a spotter in a second stage that judges whether each detection was
/// really somebody addressing the device.
public final class ConfirmedKeywordSpotter: @unchecked Sendable {

    private let spotter: any IKeywordSpotter
    private let confirmer: any IWakeConfirmer

    private let lock = NSLock()
    private var ring: [Float]
    private var written = 0                 // total samples ever accepted
    private var pending: [KwsDetection] = []

    /// A detection that survived stage two.
    public var onWoke: ((KwsDetection) -> Void)?

    /// A detection stage two threw out, and why. Surfaced rather than swallowed
    /// because "it never fires" and "it fires and is vetoed every time" are
    /// completely different problems and look identical from outside.
    public var onRejected: ((KwsDetection, String?) -> Void)?

    public var keywords: [String] { spotter.keywords }

    public var shadowedKeywords: [(phrase: String, shadowedBy: String)] {
        spotter.shadowedKeywords
    }

    public init(spotter: any IKeywordSpotter,
                confirmer: (any IWakeConfirmer)? = nil,
                historySeconds: Double = 2.0) {
        self.spotter = spotter
        self.confirmer = confirmer ?? UtteranceOnsetConfirmer()
        self.ring = [Float](repeating: 0, count: max(1, Int(historySeconds * 16_000)))

        // Collected, not judged, inside the callback: the detection arrives
        // mid-decode and stage two wants the audio AROUND it — including a
        // little that has not been decoded yet. Judging here would look only
        // backwards.
        self.spotter.onDetected = { [weak self] d in
            guard let self else { return }
            self.lock.lock()
            self.pending.append(d)
            self.lock.unlock()
        }
    }

    public func acceptWaveform(_ samples: [Float]) async {
        append(samples)
        spotter.acceptWaveform(samples)
        await drain()
    }

    public func flush() async {
        spotter.flush()
        await drain()
    }

    public func reset() {
        spotter.reset()
        lock.lock()
        pending.removeAll()
        written = 0
        for i in ring.indices { ring[i] = 0 }
        lock.unlock()
    }

    private func append(_ samples: [Float]) {
        lock.lock()
        for s in samples {
            ring[written % ring.count] = s
            written += 1
        }
        lock.unlock()
    }

    /// Pulls each pending detection out with the audio around it and asks stage
    /// two. Synchronous state is taken under the lock and released before the
    /// await, so a slow confirmer never blocks the audio thread.
    private func drain() async {
        let batch = takePending()
        guard !batch.isEmpty else { return }

        for d in batch {
            guard let candidate = candidate(for: d) else {
                // The detection has already scrolled out of the ring — only
                // possible if a caller pushes seconds at a time. Nothing to
                // judge, so it is let through rather than silently dropped.
                onWoke?(d)
                continue
            }
            if await confirmer.confirm(candidate) {
                onWoke?(d)
            } else {
                onRejected?(d, confirmer.lastReason)
            }
        }
    }

    private func takePending() -> [KwsDetection] {
        lock.lock(); defer { lock.unlock() }
        let batch = pending
        pending.removeAll()
        return batch
    }

    private func candidate(for d: KwsDetection) -> WakeCandidate? {
        lock.lock(); defer { lock.unlock() }

        let startSample = Int(d.startMs * 16)
        let endSample = Int(d.endMs * 16)

        let have = min(written, ring.count)
        let oldest = written - have
        guard have > 0, startSample >= oldest else { return nil }

        var window = [Float](repeating: 0, count: have)
        for i in 0..<have { window[i] = ring[(oldest + i) % ring.count] }

        return WakeCandidate(
            detection: d,
            window: window,
            keywordStart: startSample - oldest,
            keywordEnd: min(endSample - oldest, have))
    }
}
