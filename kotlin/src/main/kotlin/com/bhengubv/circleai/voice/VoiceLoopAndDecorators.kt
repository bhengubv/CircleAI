// VoiceLoopAndDecorators.kt
//
// The two TTS decorators, and the hands-free loop that closes the circle.
//
// Ported from src/CircleAI.Voice/{PhrasedTtsEngine, Respeller, VoiceLoop}.cs.

package com.bhengubv.circleai.voice

import java.util.concurrent.atomic.AtomicBoolean

// ─────────────────────────────────────────────────────────────────────────────
// Speaking a passage sentence by sentence

/**
 * Wraps any [ITtsEngine] so a passage is spoken sentence by sentence, with a
 * real pause where each full stop was.
 *
 * A DECORATOR rather than a change inside one engine, because the problem
 * belongs to every voice we ship: MMS, SA-11 and ToucanTTS were all trained on
 * punctuation-stripped text, so none of them can encode a pause. One
 * implementation serves all of them, and a future engine whose model DOES speak
 * punctuation simply is not wrapped.
 *
 * It also fixes a latency problem that turns out to be the same problem. Feeding
 * a whole paragraph means every word must render before the first word can play
 * — on a phone that is the difference between a pause and a stall.
 */
class PhrasedTtsEngine(
    private val inner: ITtsEngine
) : ITtsEngine, ITtsFrontEndDiagnostics {

    /** How many sentences go into one utterance. Above 1 the model renders more
     *  at a time: fewer joins, longer wait for the first word. */
    var sentencesPerUtterance: Int = 1

    /** A breath before the first word. */
    var leadInSilenceMs: Int = 0

    /** And a beat of quiet at the end, so the last syllable is allowed to decay
     *  and the listener hears the turn FINISH rather than stop. */
    var tailSilenceMs: Int = 0

    @Volatile
    var lastSegmentCount: Int = 0
        private set

    @Volatile
    override var lastSkippedCount: Int = 0
        private set

    @Volatile
    override var lastSkippedSymbols: List<String> = emptyList()
        private set

    @Volatile
    override var lastApproximatedSymbols: List<String> = emptyList()
        private set

    override suspend fun synthesise(text: String): TtsSynthesisResult {
        var segments = SentenceSplitter.split(text)
        if (sentencesPerUtterance > 1) segments = group(segments, sentencesPerUtterance)
        lastSegmentCount = segments.size
        resetDiagnostics()

        if (segments.isEmpty()) {
            return TtsSynthesisResult(ByteArray(0), 16_000, 1, 16)
        }

        // One sentence needs no joining — hand the inner result back untouched
        // so a single-sentence utterance is byte-identical to the unwrapped
        // engine.
        //
        // UNLESS breathing room was asked for. This path is easy to forget and
        // easy to hit: grouping collapses a whole paragraph to one segment, so
        // the common case lands here, and skipping the padding would apply it to
        // short text and not to long.
        if (segments.size == 1 && leadInSilenceMs <= 0 && tailSilenceMs <= 0) {
            val only = inner.synthesise(segments[0].text)
            collectDiagnostics()
            return only
        }

        val buffers = ArrayList<ByteArray>(segments.size * 2)
        var format: TtsSynthesisResult? = null
        var first = true

        for (segment in segments) {
            val part = inner.synthesise(segment.text)
            collectDiagnostics()
            if (part.audioData.isEmpty()) continue

            if (format == null) format = part

            // The breath before the first word, added once the format is known:
            // silence has to match the sample rate and width of the audio it
            // sits against, or the join is a click.
            if (first) {
                first = false
                val lead = silence(part, leadInSilenceMs)
                if (lead.isNotEmpty()) buffers.add(lead)
            }

            buffers.add(part.audioData)
            val gap = silence(part, segment.trailingPauseMs)
            if (gap.isNotEmpty()) buffers.add(gap)
        }

        val f = format ?: return TtsSynthesisResult(ByteArray(0), 16_000, 1, 16)

        val tail = silence(f, tailSilenceMs)
        if (tail.isNotEmpty()) buffers.add(tail)

        val joined = ByteArray(buffers.sumOf { it.size })
        var offset = 0
        for (b in buffers) { b.copyInto(joined, offset); offset += b.size }

        return TtsSynthesisResult(joined, f.sampleRate, f.channels, f.bitsPerSample)
    }

    /**
     * Diagnostics are ACCUMULATED across the segments of one passage. Reading
     * only the last segment's would report a clean render for a paragraph whose
     * first sentence lost every 'š' in it.
     */
    private fun collectDiagnostics() {
        val d = inner as? ITtsFrontEndDiagnostics ?: return
        lastSkippedCount += d.lastSkippedCount
        lastSkippedSymbols = (lastSkippedSymbols + d.lastSkippedSymbols).distinct()
        lastApproximatedSymbols = (lastApproximatedSymbols + d.lastApproximatedSymbols).distinct()
    }

    private fun resetDiagnostics() {
        lastSkippedCount = 0
        lastSkippedSymbols = emptyList()
        lastApproximatedSymbols = emptyList()
    }

    companion object {
        internal fun group(segments: List<SpeechSegment>, size: Int): List<SpeechSegment> {
            if (size <= 1) return segments
            val grouped = ArrayList<SpeechSegment>(segments.size / size + 1)
            var i = 0
            while (i < segments.size) {
                val take = minOf(size, segments.size - i)
                val text = segments.subList(i, i + take).joinToString(" ") { it.text }
                // The GROUP's trailing pause is the LAST member's: the pauses
                // inside are now one utterance, and the only boundary left is
                // the one at the end.
                grouped.add(SpeechSegment(text, segments[i + take - 1].trailingPauseMs))
                i += take
            }
            return grouped
        }

        /** Signed PCM is silent at zero, which is what a fresh array holds. */
        internal fun silence(format: TtsSynthesisResult, milliseconds: Int): ByteArray {
            if (milliseconds <= 0) return ByteArray(0)
            val bytesPerFrame = maxOf(1, format.channels * (format.bitsPerSample / 8))
            val frames = (format.sampleRate.toLong() * milliseconds / 1000).toInt()
            return ByteArray(frames * bytesPerFrame)
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Rewriting a borrowed word before it is spoken

/**
 * Applies a [Respeller] to everything about to be spoken.
 *
 * This existed inline in the test probe, where it improved nothing anybody
 * actually hears, because the live conversation speaks through the engine
 * directly. Both now share it, so the ear teaching the table changes what the
 * mouth says.
 */
class RespellingTtsEngine(
    val inner: ITtsEngine,
    val respeller: Respeller
) : ITtsEngine {
    override suspend fun synthesise(text: String): TtsSynthesisResult =
        inner.synthesise(respeller.rewrite(text))
}

// ─────────────────────────────────────────────────────────────────────────────
// The circle

/** One complete turn: what was heard and what was said back. */
data class VoiceExchangeEvent(
    val heard: String,
    val replied: String,
    val at: java.time.Instant = java.time.Instant.now()
)

/**
 * The full hands-free conversation, assembled:
 *
 *   wake word → VAD → ASR → BRAIN → TTS → audio out → back to listening
 *
 * [VoicePipeline] already composed the EARS and raised a transcribed event.
 * Nothing ever joined that to a brain or a mouth, so the hands-free loop did not
 * exist end to end anywhere in the codebase — each half worked in isolation and
 * no code closed the circle.
 *
 * The brain is a FUNCTION, not a service interface: the voice layer must not
 * depend on the hosting layer. The host supplies `text -> reply`.
 */
class VoiceLoop(
    private val ears: VoicePipeline,
    private val brain: suspend (String) -> String,
    private val mouth: ITtsEngine,
    private val speaker: IAudioPlayer? = null,
    /** Whether hearing the wake word while the assistant is talking stops it. */
    val allowBargeIn: Boolean = true
) : AutoCloseable {

    private val running = AtomicBoolean(false)
    private val speaking = AtomicBoolean(false)

    /** The assistant was interrupted mid-reply. */
    var onBargedIn: (() -> Unit)? = null

    /** One complete turn finished. */
    var onExchanged: ((VoiceExchangeEvent) -> Unit)? = null

    /** A turn failed. Surfaced rather than thrown, because the loop carries on:
     *  going permanently deaf is far worse than dropping one reply. */
    var onFaulted: ((Throwable) -> Unit)? = null

    val isRunning: Boolean get() = running.get()

    fun start() { running.set(true) }

    fun stop() {
        running.set(false)
        cancelSpeech()
    }

    /** Hearing the wake word while a reply is playing. Cancels ONLY the
     *  speaking: cancelling the loop would make interrupting the assistant also
     *  switch it off, which is the opposite of what the person wanted. */
    fun interruptSpeech() {
        if (allowBargeIn && speaking.compareAndSet(true, false)) onBargedIn?.invoke()
    }

    private fun cancelSpeech() { speaking.set(false) }

    /** Runs one turn. Public so a host drives the cadence and a test does not
     *  have to wait for real audio. */
    suspend fun handle(utterance: String) {
        if (!running.get()) return
        if (utterance.isBlank()) return

        try {
            val reply = brain(utterance)
            if (reply.isNotBlank()) {
                val audio = mouth.synthesise(reply)
                if (audio.audioData.isNotEmpty() && speaker != null) {
                    speaking.set(true)
                    try {
                        speaker.play(
                            audio.audioData, audio.sampleRate, audio.channels, audio.bitsPerSample
                        )
                    } finally {
                        speaking.set(false)
                    }
                }
            }
            onExchanged?.invoke(VoiceExchangeEvent(utterance, reply))
        } catch (t: Throwable) {
            // A failed turn (model hiccup, TTS fault) must not kill the loop.
            onFaulted?.invoke(t)
        }
    }

    override fun close() {
        stop()
        runCatching { ears.close() }
    }
}
