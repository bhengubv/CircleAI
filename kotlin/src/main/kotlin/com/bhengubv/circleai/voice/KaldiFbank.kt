// KaldiFbank.kt
//
// 80-dimensional log-mel filterbank features, bit-compatible with Kaldi.
//
// WHY NOT THE MEL WE ALREADY HAVE. The speaker-identity path computes a mel
// spectrogram and it is a perfectly good generic one: Hamming window, plain hop,
// no pre-emphasis, no DC removal. Feeding that to a Kaldi-trained model produces
// features of the right SHAPE and the wrong NUMBERS — so the model loads, runs,
// burns battery and never fires. Nothing errors. That failure looks exactly like
// "the wake word is not very good", which is why this is written out properly
// rather than approximated.
//
// The five details that decide whether this works, each a silent killer alone:
//
//   highFreq = -400    NEGATIVE means nyquist + highFreq, so the top of the mel
//                      range is 7600 Hz, not 8000. Wrong here shifts every filter.
//   snipEdges = false  Frames are CENTRED, the first starts at -120, and
//                      out-of-range samples are MIRRORED, not zero-padded.
//   NO x 32768         Samples go in at [-1, 1] and are used as they are.
//   povey window       (0.5 - 0.5cos)^0.85, not Hamming, not Hann.
//   preemph + DC       Per frame, in that order: subtract the mean, then
//                      pre-emphasise at 0.97, THEN window.
//
// Ported from src/CircleAI.Voice/KaldiFbank.cs.

package com.bhengubv.circleai.voice

import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.ln
import kotlin.math.max
import kotlin.math.pow
import kotlin.math.sin
import kotlin.math.sqrt

data class KaldiFbankOptions(
    val sampleRateHz: Int = 16_000,
    val numMelBins: Int = 80,
    val lowFreqHz: Float = 20.0f,
    /** NEGATIVE means nyquist + this. See [resolvedHighFreq]. */
    val highFreqHz: Float = -400.0f,
    val frameLengthMs: Float = 25.0f,
    val frameShiftMs: Float = 10.0f,
    val preemphasisCoefficient: Float = 0.97f,
    val removeDcOffset: Boolean = true,
    val snipEdges: Boolean = false,
    /** OFF by default. Samples arrive at [-1, 1] and Kaldi uses them as they are. */
    val scaleToInt16: Boolean = false
) {
    val frameLength: Int get() = (sampleRateHz * frameLengthMs / 1000).toInt()
    val frameShift: Int get() = (sampleRateHz * frameShiftMs / 1000).toInt()

    val paddedWindow: Int
        get() {
            var n = 1
            while (n < frameLength) n = n shl 1
            return n
        }

    /**
     * A positive value is a frequency; a NEGATIVE one is an offset down from
     * nyquist. -400 at 16 kHz means 7600 Hz, not -400 Hz.
     */
    val resolvedHighFreq: Float
        get() = if (highFreqHz > 0) highFreqHz else sampleRateHz / 2f + highFreqHz
}

class KaldiFbank(private val o: KaldiFbankOptions = KaldiFbankOptions()) {

    private val window: FloatArray = poveyWindow(o.frameLength)
    private val melBanks: Array<FloatArray>
    private val melStart: IntArray

    private var samples = FloatArray(0)
    private var sampleCount = 0
    private var framesRead = 0

    var framesReady: Int = 0
        private set

    init {
        val (banks, start) = melBanks(o)
        melBanks = banks
        melStart = start
    }

    val dimension: Int get() = o.numMelBins

    fun acceptWaveform(incoming: FloatArray) {
        // If scaling is asked for it belongs HERE, once, before anything reads a
        // sample — everything downstream inherits the factor.
        val scale = if (o.scaleToInt16) 32768f else 1f

        if (sampleCount + incoming.size > samples.size) {
            samples = samples.copyOf(max(samples.size * 2, sampleCount + incoming.size))
        }
        for (i in incoming.indices) samples[sampleCount + i] = incoming[i] * scale
        sampleCount += incoming.size

        recount(flush = false)
    }

    fun flush() = recount(flush = true)

    fun reset() {
        sampleCount = 0
        framesRead = 0
        framesReady = 0
    }

    private fun recount(flush: Boolean) {
        val n = sampleCount
        var frames: Int
        if (o.snipEdges) {
            frames = if (n < o.frameLength) 0 else 1 + (n - o.frameLength) / o.frameShift
        } else if (flush) {
            // Kaldi's count for a complete utterance.
            frames = (n + o.frameShift / 2) / o.frameShift
        } else {
            // Mid-stream: only frames whose window is entirely inside the audio
            // actually held. The mirrored tail is deliberately withheld, because
            // a frame computed from a mirror that later has real audio behind it
            // is a DIFFERENT frame, and a streaming detector cannot take it back.
            frames = 0
            while (firstSample(frames) + o.frameLength <= n) frames++
        }
        framesReady = max(0, frames)
    }

    /** CENTRED when snipEdges is off: the midpoint minus half a window, so frame
     *  0 starts at -120 and is filled by mirroring. */
    internal fun firstSample(frame: Int): Int =
        if (o.snipEdges) frame * o.frameShift
        else frame * o.frameShift + o.frameShift / 2 - o.frameLength / 2

    fun frame(index: Int): FloatArray? {
        if (index < 0 || index >= framesReady) return null

        val n = sampleCount
        val start = firstSample(index)
        val buf = FloatArray(o.paddedWindow)      // zero-padded to the FFT size

        for (i in 0 until o.frameLength) {
            var s = start + i
            // Kaldi MIRRORS rather than zero-padding. Looping, because a very
            // short utterance can reflect off both ends more than once.
            while (s < 0 || s >= n) {
                s = if (s < 0) -s - 1 else 2 * n - 1 - s
            }
            buf[i] = samples[s]
        }

        // Order matters and is Kaldi's: mean, then pre-emphasis, then window.
        if (o.removeDcOffset) {
            var sum = 0f
            for (i in 0 until o.frameLength) sum += buf[i]
            val mean = sum / o.frameLength
            for (i in 0 until o.frameLength) buf[i] -= mean
        }

        if (o.preemphasisCoefficient != 0f) {
            val c = o.preemphasisCoefficient
            for (i in o.frameLength - 1 downTo 1) buf[i] -= c * buf[i - 1]
            buf[0] -= c * buf[0]                  // Kaldi repeats sample 0
        }

        for (i in 0 until o.frameLength) buf[i] *= window[i]

        val power = powerSpectrum(buf)
        val out = FloatArray(o.numMelBins)
        for (m in 0 until o.numMelBins) {
            val bank = melBanks[m]
            val first = melStart[m]
            var e = 0f
            for (k in bank.indices) e += power[first + k] * bank[k]
            // Float.ulp of 1.0 (1.19e-7), NOT the denormal minimum (1.4e-45).
            // Kaldi uses numeric_limits<float>::epsilon(); the denormal minimum
            // is a completely different floor and would change every silent
            // frame's value.
            out[m] = ln(max(e, FLOAT_EPSILON).toDouble()).toFloat()
        }
        return out
    }

    fun consume(frames: Int) {
        if (frames <= 0) return
        framesRead += frames
        val keepFrom = max(0, firstSample(framesRead))
        if (keepFrom <= 0) return

        val keep = max(0, sampleCount - keepFrom)
        System.arraycopy(samples, keepFrom, samples, 0, keep)
        sampleCount = keep
        // Indices are relative to the buffer, so the frame origin shifts with it.
        framesRead = 0
        recount(flush = false)
    }

    companion object {
        /** `Float.ulp` of 1.0f — what `numeric_limits<float>::epsilon()` is. */
        const val FLOAT_EPSILON = 1.1920929e-7f

        fun poveyWindow(n: Int): FloatArray {
            val w = FloatArray(n)
            val a = 2 * Math.PI / (n - 1)
            for (i in 0 until n) {
                w[i] = (0.5 - 0.5 * cos(a * i)).pow(0.85).toFloat()
            }
            return w
        }

        fun melScale(hz: Float): Float = (1127.0 * ln(1.0 + hz / 700.0)).toFloat()

        fun melBanks(o: KaldiFbankOptions): Pair<Array<FloatArray>, IntArray> {
            val fftBins = o.paddedWindow / 2
            val binWidth = o.sampleRateHz.toFloat() / o.paddedWindow

            val melLow = melScale(o.lowFreqHz)
            val melHigh = melScale(o.resolvedHighFreq)
            val delta = (melHigh - melLow) / (o.numMelBins + 1)

            val banks = ArrayList<FloatArray>(o.numMelBins)
            val start = IntArray(o.numMelBins)

            for (m in 0 until o.numMelBins) {
                val left = melLow + m * delta
                val centre = melLow + (m + 1) * delta
                val right = melLow + (m + 2) * delta

                val weights = ArrayList<Float>()
                var first = -1
                for (i in 0 until fftBins) {
                    val mel = melScale(binWidth * i)
                    if (mel <= left || mel >= right) {
                        if (first >= 0) break                 // past the triangle
                        continue
                    }
                    if (first < 0) first = i
                    weights.add(
                        if (mel <= centre) (mel - left) / (centre - left)
                        else (right - mel) / (right - centre)
                    )
                }
                banks.add(weights.toFloatArray())
                start[m] = if (first < 0) 0 else first
            }
            return banks.toTypedArray() to start
        }

        /**
         * Radix-2, in place. Written out rather than taken from a library so the
         * numbers are identical on every platform this ports to — a vendor FFT
         * is free to reassociate, and a 1-ULP difference here is a different
         * feature vector.
         */
        fun powerSpectrum(frame: FloatArray): FloatArray {
            val n = frame.size
            val re = frame.copyOf()
            val im = FloatArray(n)

            // Bit-reversal permutation.
            var j = 0
            for (i in 1 until n) {
                var bit = n shr 1
                while (j and bit != 0) {
                    j = j xor bit
                    bit = bit shr 1
                }
                j = j xor bit
                if (i < j) {
                    val tr = re[i]; re[i] = re[j]; re[j] = tr
                    val ti = im[i]; im[i] = im[j]; im[j] = ti
                }
            }

            var len = 2
            while (len <= n) {
                val ang = -2 * Math.PI / len
                val wRe = cos(ang).toFloat()
                val wIm = sin(ang).toFloat()
                var i = 0
                while (i < n) {
                    var curRe = 1f
                    var curIm = 0f
                    for (k in 0 until len / 2) {
                        val uRe = re[i + k]
                        val uIm = im[i + k]
                        val vRe = re[i + k + len / 2] * curRe - im[i + k + len / 2] * curIm
                        val vIm = re[i + k + len / 2] * curIm + im[i + k + len / 2] * curRe
                        re[i + k] = uRe + vRe
                        im[i + k] = uIm + vIm
                        re[i + k + len / 2] = uRe - vRe
                        im[i + k + len / 2] = uIm - vIm
                        val nextRe = curRe * wRe - curIm * wIm
                        curIm = curRe * wIm + curIm * wRe
                        curRe = nextRe
                    }
                    i += len
                }
                len = len shl 1
            }

            val power = FloatArray(n / 2 + 1)
            for (k in 0..n / 2) power[k] = re[k] * re[k] + im[k] * im[k]
            return power
        }
    }
}
