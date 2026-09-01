// VoiceTtsDecorators.swift
//
// Two wrappers that go around ANY ITtsEngine, because the problems they solve
// belong to every voice we ship rather than to one engine.
//
// PhrasedTtsEngine — MMS, guymandude SA-11 and ToucanTTS were all trained on
// punctuation-stripped text, so none of them can encode a pause. A decorator
// means one implementation serves all of them, and a future engine whose model
// DOES speak punctuation can simply not be wrapped. It also fixes a latency
// problem that turns out to be the same problem: feeding a whole paragraph to
// the model means every word must render before the first word can play, which
// on a phone is the difference between a pause and a stall.
//
// RespellingTtsEngine — one place where a borrowed word is rewritten for the
// host voice. This existed inline in the test probe, where it improved nothing
// anybody actually hears, because the live conversation speaks through the
// engine directly. Both now share it, so the ear teaching the table changes what
// the mouth says.
//
// Ported from src/CircleAI.Voice/PhrasedTtsEngine.cs and Respeller.cs.

import Foundation

// MARK: - Rewriting a passage before it is spoken

public extension Respeller {

    /// Rewrites every foreign word in a passage so the host voice can say it.
    ///
    /// A language these spellings were never written for is left COMPLETELY
    /// alone — not even the compound splitting. Afrikaans has its own forms for
    /// these words, and "S.M.S." is our idea of helpful imposed on a language
    /// that did not ask for it. Guarded here rather than only at the factory, so
    /// a caller that builds a Respeller directly cannot bypass it.
    func rewrite(_ text: String?) -> String {
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return text ?? ""
        }
        guard LoanwordRespeller.isNguniOrSotho(hostLanguage) else { return text }

        var built = ""
        built.reserveCapacity(text.count + 16)

        for span in LanguageSpanSplitter.split(text) {
            guard span.isForeign else { built.append(span.text); continue }

            let word = span.text.trimmingCharacters(in: .whitespacesAndNewlines)
            if let respelt = respelling(for: word) {
                log?("respelt \"\(word)\" as \"\(respelt)\"")
                // The span's own leading and trailing spacing is preserved, so
                // rewriting a word does not silently glue it to its neighbour.
                built.append(span.text.replacingOccurrences(of: word, with: respelt))
            } else {
                built.append(LanguageSpanSplitter.toSpokenForm(span.text))
            }
        }
        return built
    }
}

/// Applies a `Respeller` to everything about to be spoken.
public final class RespellingTtsEngine: ITtsEngine, @unchecked Sendable {

    public let inner: any ITtsEngine
    public let respeller: Respeller

    public init(inner: any ITtsEngine, respeller: Respeller) {
        self.inner = inner
        self.respeller = respeller
    }

    public func synthesise(text: String) async throws -> TtsSynthesisResult {
        try await inner.synthesise(text: respeller.rewrite(text))
    }

    public func streamSynthesise(text: String) -> AsyncThrowingStream<Data, Error> {
        inner.streamSynthesise(text: respeller.rewrite(text))
    }
}

// MARK: - Speaking a passage sentence by sentence

public final class PhrasedTtsEngine: ITtsEngine, ITtsFrontEndDiagnostics, @unchecked Sendable {

    private let inner: any ITtsEngine

    private let lock = NSLock()
    private var segmentCount = 0
    private var skippedCount = 0
    private var skipped: [String] = []
    private var approximated: [String] = []

    /// How many sentences go into one utterance. Above 1 the model renders more
    /// at a time, which is fewer joins and a longer wait for the first word.
    public var sentencesPerUtterance = 1

    /// A breath before the first word.
    public var leadInSilenceMs = 0

    /// And a beat of quiet at the end, so the last syllable is allowed to decay
    /// and the listener hears the turn FINISH rather than stop.
    public var tailSilenceMs = 0

    public init(inner: any ITtsEngine) {
        self.inner = inner
    }

    public var lastSegmentCount: Int {
        lock.lock(); defer { lock.unlock() }
        return segmentCount
    }

    public var lastSkippedCount: Int {
        lock.lock(); defer { lock.unlock() }
        return skippedCount
    }

    public var lastSkippedSymbols: [String] {
        lock.lock(); defer { lock.unlock() }
        return skipped
    }

    public var lastApproximatedSymbols: [String] {
        lock.lock(); defer { lock.unlock() }
        return approximated
    }

    /// Diagnostics are ACCUMULATED across the segments of one passage. Reading
    /// only the last segment's would report a clean render for a paragraph whose
    /// first sentence lost every 'š' in it.
    private func collectDiagnostics() {
        guard let d = inner as? any ITtsFrontEndDiagnostics else { return }
        lock.lock()
        skippedCount += d.lastSkippedCount
        for s in d.lastSkippedSymbols where !skipped.contains(s) { skipped.append(s) }
        for s in d.lastApproximatedSymbols where !approximated.contains(s) { approximated.append(s) }
        lock.unlock()
    }

    private func resetDiagnostics() {
        lock.lock()
        skippedCount = 0
        skipped.removeAll()
        approximated.removeAll()
        lock.unlock()
    }

    private func note(segmentCount n: Int) {
        lock.lock(); segmentCount = n; lock.unlock()
    }

    static func group(_ segments: [SpeechSegment], size: Int) -> [SpeechSegment] {
        guard size > 1 else { return segments }
        var grouped: [SpeechSegment] = []
        grouped.reserveCapacity(segments.count / size + 1)
        var i = 0
        while i < segments.count {
            let take = min(size, segments.count - i)
            let text = segments[i..<(i + take)].map(\.text).joined(separator: " ")
            // The GROUP's trailing pause is the LAST member's, not the first's:
            // the pauses inside the group are now spoken as one utterance and
            // the only boundary left is the one at the end.
            grouped.append(SpeechSegment(text: text,
                                         trailingPauseMs: segments[i + take - 1].trailingPauseMs))
            i += take
        }
        return grouped
    }

    public func synthesise(text: String) async throws -> TtsSynthesisResult {
        var segments = SentenceSplitter.split(text)
        if sentencesPerUtterance > 1 { segments = Self.group(segments, size: sentencesPerUtterance) }
        note(segmentCount: segments.count)
        resetDiagnostics()

        if segments.isEmpty {
            return TtsSynthesisResult(audioData: Data(), sampleRate: 16_000,
                                      channels: 1, bitsPerSample: 16)
        }

        // One sentence needs no joining — hand the inner result back untouched so
        // a single-sentence utterance is byte-identical to the unwrapped engine.
        //
        // UNLESS breathing room was asked for. This path is easy to forget and
        // easy to hit: grouping sentences collapses a whole paragraph to a single
        // segment, so the common case ends up here, and skipping the padding
        // would silently apply it to short text and not to long.
        if segments.count == 1 && leadInSilenceMs <= 0 && tailSilenceMs <= 0 {
            let only = try await inner.synthesise(text: segments[0].text)
            collectDiagnostics()
            return only
        }

        var buffers: [Data] = []
        buffers.reserveCapacity(segments.count * 2)
        var format: TtsSynthesisResult?
        var first = true

        for segment in segments {
            try Task.checkCancellation()

            let part = try await inner.synthesise(text: segment.text)
            collectDiagnostics()
            if part.audioData.isEmpty { continue }

            if format == nil { format = part }

            // The breath before the first word, added once the format is known:
            // silence has to match the sample rate and width of the audio it sits
            // against or the join is a click.
            if first {
                first = false
                let lead = Self.silence(part, milliseconds: leadInSilenceMs)
                if !lead.isEmpty { buffers.append(lead) }
            }

            buffers.append(part.audioData)

            let gap = Self.silence(part, milliseconds: segment.trailingPauseMs)
            if !gap.isEmpty { buffers.append(gap) }
        }

        guard let format else {
            return TtsSynthesisResult(audioData: Data(), sampleRate: 16_000,
                                      channels: 1, bitsPerSample: 16)
        }

        let tail = Self.silence(format, milliseconds: tailSilenceMs)
        if !tail.isEmpty { buffers.append(tail) }

        var joined = Data()
        joined.reserveCapacity(buffers.reduce(0) { $0 + $1.count })
        for b in buffers { joined.append(b) }

        return TtsSynthesisResult(audioData: joined, sampleRate: format.sampleRate,
                                  channels: format.channels, bitsPerSample: format.bitsPerSample)
    }

    public func streamSynthesise(text: String) -> AsyncThrowingStream<Data, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                let segments = SentenceSplitter.split(text)
                self.note(segmentCount: segments.count)
                self.resetDiagnostics()

                do {
                    for segment in segments {
                        try Task.checkCancellation()

                        // Synthesised PER SENTENCE rather than delegated to the
                        // inner stream: the inner engine renders whatever it is
                        // given in one pass, so passing the whole passage would
                        // reinstate exactly the stall this avoids.
                        let part = try await self.inner.synthesise(text: segment.text)
                        self.collectDiagnostics()
                        if part.audioData.isEmpty { continue }

                        continuation.yield(part.audioData)

                        let gap = Self.silence(part, milliseconds: segment.trailingPauseMs)
                        if !gap.isEmpty { continuation.yield(gap) }
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// Signed PCM is silent at zero, which is also what a fresh buffer holds.
    static func silence(_ format: TtsSynthesisResult, milliseconds: Int) -> Data {
        guard milliseconds > 0 else { return Data() }
        let bytesPerFrame = max(1, format.channels * (format.bitsPerSample / 8))
        let frames = Int(Int64(format.sampleRate) * Int64(milliseconds) / 1000)
        return Data(count: frames * bytesPerFrame)
    }
}
