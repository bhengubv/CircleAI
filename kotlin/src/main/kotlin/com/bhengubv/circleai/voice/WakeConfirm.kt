// WakeConfirm.kt
//
// A keyword spotter fires on sound that RESEMBLES the phrase. A confirmer
// decides whether it was somebody addressing the device, or the phrase turning
// up in the middle of a sentence about the device.
//
// Port of CircleAI.Voice/ConfirmedKeywordSpotter.cs. The acoustic stage stays
// behind IKeywordSpotter, because that half is an ONNX model; everything the
// decision depends on is here and runs anywhere.

package com.bhengubv.circleai.voice

import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sqrt

/** One keyword, as loaded from the keyword list. */
data class KwsKeyword(
    val tokens: List<Int>,
    val phrase: String,
    val boost: Float = 0f,
    val threshold: Float = 0f,
)

/** A keyword was heard. */
data class KwsDetection(
    val phrase: String,
    val atFrame: Int,
    val probability: Double,
    /** -1 when the spotter did not report a start; the end is used instead. */
    val startFrame: Int = -1,
) {
    /** Where the phrase began, in milliseconds from the start of the stream. */
    val startMs: Double get() = (if (startFrame < 0) atFrame else startFrame) * MS_PER_FRAME

    /** Where the phrase ended, in milliseconds from the start of the stream. */
    val endMs: Double get() = atFrame * MS_PER_FRAME

    companion object {
        /** Milliseconds per encoder frame - 4x subsampling of 10 ms hops. */
        const val MS_PER_FRAME = 40.0
    }
}

/**
 * The leading hypothesis walking into a keyword.
 *
 * This is how a threshold gets set from EVIDENCE instead of taste: it shows the
 * score a phrase actually reaches on real speech, so the threshold can be
 * placed between the hits and the misses rather than at whatever number the
 * upstream project happened to pick.
 */
data class KwsProgress(
    val phrase: String,
    val matched: Int,
    val total: Int,
    val meanProbability: Double,
)

/** The acoustic stage. Implemented on-device by the Zipformer spotter. */
interface IKeywordSpotter : AutoCloseable {
    val keywords: List<String>

    /** Registered phrases that can never fire because another shadows them. Empty is healthy. */
    val shadowedKeywords: List<Pair<String, String>>

    fun acceptWaveform(samples: FloatArray)
    fun flush()
    fun reset()

    /** Called for each detection as it is decoded. */
    var onDetected: ((KwsDetection) -> Unit)?
}

/** The audio window a detection sits in, with the phrase located inside it. */
class WakeCandidate(
    val detection: KwsDetection,
    /** 16 kHz mono float, oldest sample first. */
    val window: FloatArray,
    val keywordStart: Int,
    val keywordEnd: Int,
)

/** Confirms or rejects a candidate, and says why when it rejects. */
interface IWakeConfirmer {
    suspend fun confirm(candidate: WakeCandidate): Boolean
    val lastReason: String?
}

/** Confirms everything. For when the spotter is already strict enough. */
class AlwaysConfirm : IWakeConfirmer {
    override val lastReason: String? get() = null
    override suspend fun confirm(candidate: WakeCandidate): Boolean = true
}

/**
 * Rejects a phrase that arrived in the MIDDLE of an utterance.
 *
 * Somebody addressing a device pauses first. The same words inside a running
 * sentence are almost always ABOUT the device rather than to it - which is why
 * what gets measured here is how long the person had already been talking, not
 * how confident the acoustic model was.
 */
class UtteranceOnsetConfirmer(
    /** Longer than this much continuous speech before the phrase ended and it was not an address. */
    var maxLeadInMs: Double = 600.0,
    /** A quiet stretch shorter than this does not count as a pause. */
    var gapToleranceMs: Double = 150.0,
    /** Speech is anything above this fraction of the window peak. */
    var speechFloor: Double = 0.12,
) : IWakeConfirmer {

    private val lock = Any()
    private var reason: String? = null

    override val lastReason: String? get() = synchronized(lock) { reason }

    private fun setReason(r: String?) { synchronized(lock) { reason = r } }

    override suspend fun confirm(candidate: WakeCandidate): Boolean {
        val w = candidate.window

        // Nothing to judge means FAIL OPEN. A confirmer that rejects when it
        // cannot see is a device that stops answering.
        if (w.isEmpty()) { setReason(null); return true }

        val per = BUCKET_MS * (SAMPLE_RATE / 1000) // samples per 10 ms bucket
        val n = w.size / per
        if (n < 4) { setReason(null); return true }

        val rms = FloatArray(n)
        var peak = 0f
        for (b in 0 until n) {
            var s = 0.0
            for (i in (b * per) until ((b + 1) * per)) s += w[i].toDouble() * w[i].toDouble()
            rms[b] = sqrt(s / per).toFloat()
            if (rms[b] > peak) peak = rms[b]
        }

        if (peak <= 1e-6f) { setReason("silence"); return false }

        val floor = peak * speechFloor.toFloat()
        val gap = max(1, (gapToleranceMs / BUCKET_MS).toInt())
        val endBucket = min(max(candidate.keywordEnd / per, 0), n - 1)

        // Walk BACKWARDS from the phrase end to find where the speech began.
        var onset = endBucket
        var quiet = 0
        var b = endBucket
        while (b >= 0) {
            if (rms[b] >= floor) { onset = b; quiet = 0 } else {
                quiet++
                if (quiet >= gap) break
            }
            b--
        }

        val leadIn = ((endBucket - onset + 1) * BUCKET_MS).toDouble()
        if (leadIn <= maxLeadInMs) { setReason(null); return true }

        setReason(
            "had been speaking " + leadIn.toInt() + " ms before the phrase ended (max " +
                maxLeadInMs.toInt() + ")",
        )
        return false
    }

    companion object {
        const val BUCKET_MS = 10
        const val SAMPLE_RATE = 16_000
    }
}

/**
 * Re-transcribes the window and checks the phrase is how the utterance STARTS,
 * allowing a few filler words in front of it.
 */
class TranscriptConfirmer(
    private val transcribe: suspend (ByteArray) -> String,
    private val normalise: (String) -> String = { s ->
        // Everything that is not a letter or a digit becomes a space, so
        // punctuation and casing cannot make a match fail.
        s.lowercase().map { if (it.isLetterOrDigit()) it else Char(32) }.joinToString("")
    },
) : IWakeConfirmer {

    /** Fillers a person really does say before addressing a device. */
    var allowedLeadIn: Set<String> = setOf(
        "um", "uh", "er", "erm", "ah", "oh", "hey", "ok", "okay", "so", "please", "yeah",
    )

    private val lock = Any()
    private var reason: String? = null

    override val lastReason: String? get() = synchronized(lock) { reason }

    private fun setReason(r: String?) { synchronized(lock) { reason = r } }

    override suspend fun confirm(candidate: WakeCandidate): Boolean {
        return try {
            val text = transcribe(toPcm16(candidate.window))

            val heard = normalise(text).split(Char(32)).filter { it.isNotEmpty() }
            val phrase = normalise(candidate.detection.phrase).split(Char(32)).filter { it.isNotEmpty() }

            // Nothing to judge - fail open rather than refusing to wake.
            if (heard.isEmpty() || phrase.isEmpty()) { setReason(null); return true }

            var at = 0
            while (at < heard.size && allowedLeadIn.contains(heard[at])) at++

            if (at + phrase.size <= heard.size) {
                var match = true
                for (j in phrase.indices) {
                    if (heard[at + j] != phrase[j]) { match = false; break }
                }
                if (match) { setReason(null); return true }
            }

            setReason(
                "heard " + heard.take(6).joinToString(" ") + " - phrase is not how it starts",
            )
            false
        } catch (e: Exception) {
            // A confirmer that is UNAVAILABLE must not silence the device.
            setReason("confirmer unavailable (" + (e::class.simpleName ?: "error") + ") - allowed")
            true
        }
    }

    companion object {
        /** PCM-16 little-endian, which is what every transcriber here takes. */
        fun toPcm16(samples: FloatArray): ByteArray {
            val out = ByteArray(samples.size * 2)
            for (i in samples.indices) {
                val v = max(-32768.0, min(32767.0, (samples[i] * 32767f).toDouble().roundToInt().toDouble())).toInt()
                out[i * 2] = (v and 0xFF).toByte()
                out[i * 2 + 1] = ((v shr 8) and 0xFF).toByte()
            }
            return out
        }
    }
}

/**
 * Two confirmers in series: the CHEAP one first, then the PRECISE one.
 *
 * BOTH must agree. The name says either; the C# requires both, and this port
 * matches the code rather than the name. The cheap check exists to avoid paying
 * for the expensive one on the many candidates it can reject outright.
 */
class EitherConfirmer(
    private val cheap: IWakeConfirmer,
    private val precise: IWakeConfirmer,
) : IWakeConfirmer {

    private val lock = Any()
    private var reason: String? = null

    override val lastReason: String? get() = synchronized(lock) { reason }

    private fun setReason(r: String?) { synchronized(lock) { reason = r } }

    override suspend fun confirm(candidate: WakeCandidate): Boolean {
        if (!cheap.confirm(candidate)) { setReason(cheap.lastReason); return false }
        if (!precise.confirm(candidate)) { setReason(precise.lastReason); return false }
        setReason(null)
        return true
    }
}

/**
 * Two-stage wake: an acoustic spotter, then a confirmer over the audio around
 * the detection.
 *
 * The ring buffer is the reason this class exists. A detection arrives
 * mid-decode and stage two wants the audio AROUND it, including a little that
 * has not been decoded yet - so detections are COLLECTED inside the callback
 * and judged afterwards. Judging inside the callback would look only backwards.
 */
class ConfirmedKeywordSpotter(
    private val spotter: IKeywordSpotter,
    private val confirmer: IWakeConfirmer = UtteranceOnsetConfirmer(),
    /**
     * How much recent audio to keep for stage two. Two seconds covers the
     * longest wake phrase plus its run-up with room to spare, and costs 128 KB.
     */
    historySeconds: Double = 2.0,
) : AutoCloseable {

    private val ring = FloatArray((historySeconds * 16_000).toInt())
    private var written = 0
    private val pending = mutableListOf<KwsDetection>()

    /** A wake word was heard AND confirmed. */
    var onWoke: ((KwsDetection) -> Unit)? = null

    /**
     * Stage one fired but stage two turned it down, with the reason.
     *
     * Surfaced deliberately: a rejection is the single most useful signal for
     * tuning a wake word, and one that is silently swallowed leaves "it does
     * not wake" and "it woke and we vetoed it" looking identical from outside.
     */
    var onRejected: ((KwsDetection, String?) -> Unit)? = null

    val keywords: List<String> get() = spotter.keywords

    val shadowedKeywords: List<Pair<String, String>> get() = spotter.shadowedKeywords

    init {
        spotter.onDetected = { d -> pending.add(d) }
    }

    /** Feeds audio. Float samples in -1..1 at 16 kHz. */
    suspend fun acceptWaveform(samples: FloatArray) {
        append(samples)
        spotter.acceptWaveform(samples)
        drain()
    }

    /** Marks the end of the audio and judges anything outstanding. */
    suspend fun flush() {
        spotter.flush()
        drain()
    }

    /** Clears stream state for a new utterance, keeping the loaded models. */
    fun reset() {
        spotter.reset()
        pending.clear()
        written = 0
        ring.fill(0f)
    }

    override fun close() = spotter.close()

    private fun append(samples: FloatArray) {
        for (s in samples) {
            ring[written % ring.size] = s
            written++
        }
    }

    private suspend fun drain() {
        if (pending.isEmpty()) return
        val batch = pending.toList()
        pending.clear()

        for (d in batch) {
            val startSample = (d.startMs * 16).toInt()
            val endSample = (d.endMs * 16).toInt()

            val have = min(written, ring.size)
            val oldest = written - have

            // Already scrolled out of the ring - only possible if a caller
            // pushes seconds at a time. There is nothing to judge, so it is LET
            // THROUGH rather than silently dropped.
            if (startSample < oldest) { onWoke?.invoke(d); continue }

            val window = FloatArray(have)
            for (i in 0 until have) window[i] = ring[(oldest + i) % ring.size]

            val candidate = WakeCandidate(
                d, window, startSample - oldest, min(endSample - oldest, have),
            )

            if (confirmer.confirm(candidate)) onWoke?.invoke(d)
            else onRejected?.invoke(d, confirmer.lastReason)
        }
    }
}
