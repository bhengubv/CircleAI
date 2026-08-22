// WavIo.kt
//
// Kotlin port of src/CircleAI.Voice/WavIo.cs — minimal RIFF/WAVE reading and
// PCM-16 packing, so a reference recording can become the float samples a voice
// needs.
//
// Parity is asserted against fixtures/voice_wav_io.json.

package com.bhengubv.circleai.android.voice

import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/** Interleaved float samples in [-1,1], plus rate and channel count. */
data class Wav(val samples: FloatArray, val rate: Int, val channels: Int) {
    override fun equals(other: Any?): Boolean =
        other is Wav && samples.contentEquals(other.samples) &&
            rate == other.rate && channels == other.channels

    override fun hashCode(): Int =
        samples.contentHashCode() * 31 * 31 + rate * 31 + channels
}

object WavIo {

    /** Mimi's sample rate — what [toMono24k] resamples to. */
    const val TARGET_RATE = 24000

    /** Parse a RIFF/WAVE buffer. */
    fun parse(raw: ByteArray): Wav {
        require(raw.size >= 12) { "not a RIFF/WAVE file" }
        val bb = ByteBuffer.wrap(raw)
        require(bb.order(ByteOrder.BIG_ENDIAN).getInt(0) == 0x52494646) { "not a RIFF/WAVE file" }
        require(bb.getInt(8) == 0x57415645) { "not a RIFF/WAVE file" }

        var format = 0
        var channels = 0
        var rate = 0
        var bits = 0
        var dataStart = -1
        var dataSize = 0
        var offset = 12

        // WALK THE CHUNKS. A WAV written by anything other than the simplest
        // encoder carries LIST/fact/cue chunks before the data, and assuming data
        // starts at byte 44 reads metadata as audio — which sounds like a short
        // burst of noise before the real recording.
        while (offset + 8 <= raw.size) {
            val id = bb.order(ByteOrder.BIG_ENDIAN).getInt(offset)
            var size = bb.order(ByteOrder.LITTLE_ENDIAN).getInt(offset + 4)
            val body = offset + 8
            if (size < 0 || body + size > raw.size) size = raw.size - body

            when (id) {
                0x666D7420 -> {                    // "fmt "
                    bb.order(ByteOrder.LITTLE_ENDIAN)
                    format = bb.getShort(body).toInt() and 0xFFFF
                    channels = bb.getShort(body + 2).toInt() and 0xFFFF
                    rate = bb.getInt(body + 4)
                    bits = bb.getShort(body + 14).toInt() and 0xFFFF
                }
                0x64617461 -> {                    // "data"
                    dataStart = body
                    dataSize = size
                }
            }

            offset = body + size + (size and 1)    // chunks are word-aligned
        }

        require(channels != 0 && rate != 0 && dataStart >= 0 && dataSize != 0) {
            "no usable fmt/data chunk"
        }
        bb.order(ByteOrder.LITTLE_ENDIAN)

        // 3 is IEEE float; 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose real format
        // lives in a sub-chunk — treated as PCM here, which is what it is in every
        // file the voice stack has met.
        val pcm = format == 1 || format == 0xFFFE
        val samples: FloatArray = when {
            pcm && bits == 8 -> FloatArray(dataSize) {
                ((raw[dataStart + it].toInt() and 0xFF) - 128) / 128f
            }
            pcm && bits == 16 -> FloatArray(dataSize / 2) {
                bb.getShort(dataStart + it * 2) / 32768f
            }
            pcm && bits == 24 -> FloatArray(dataSize / 3) {
                val o = dataStart + it * 3
                val v = (raw[o].toInt() and 0xFF) or
                    ((raw[o + 1].toInt() and 0xFF) shl 8) or
                    ((raw[o + 2].toInt() and 0xFF) shl 16)
                (v shl 8 shr 8) / 8388608f
            }
            pcm && bits == 32 -> FloatArray(dataSize / 4) {
                bb.getInt(dataStart + it * 4) / 2147483648f
            }
            format == 3 && bits == 32 -> FloatArray(dataSize / 4) {
                bb.getFloat(dataStart + it * 4)
            }
            else -> throw IllegalArgumentException(
                "WAV format $format at $bits bits is not decoded by this reader"
            )
        }

        return Wav(samples, rate, channels)
    }

    /** Downmix to mono, resample to 24 kHz, and cap the length. */
    fun toMono24k(wav: Wav, maxSeconds: Int = 30): FloatArray {
        var samples = wav.samples

        if (wav.channels > 1) {
            val mono = FloatArray(samples.size / wav.channels)
            for (i in mono.indices) {
                var sum = 0f
                for (c in 0 until wav.channels) sum += samples[i * wav.channels + c]
                mono[i] = sum / wav.channels
            }
            samples = mono
        }

        if (wav.rate != TARGET_RATE) samples = resample(samples, wav.rate, TARGET_RATE)

        val cap = maxSeconds * TARGET_RATE
        return if (samples.size > cap) samples.copyOf(cap) else samples
    }

    /** Read a WAV file as mono float samples at 24 kHz. */
    fun readMono24k(path: String, maxSeconds: Int = 30): FloatArray =
        toMono24k(parse(File(path).readBytes()), maxSeconds)

    /** Pack float samples in [-1,1] as little-endian signed 16-bit PCM. */
    fun toPcm16(samples: FloatArray): ByteArray {
        val out = ByteArray(samples.size * 2)
        val bb = ByteBuffer.wrap(out).order(ByteOrder.LITTLE_ENDIAN)
        for (i in samples.indices) {
            bb.putShort(i * 2, (max(-1f, min(1f, samples[i])) * 32767f).toInt().toShort())
        }
        return out
    }

    /** Linear resample. Adequate here: the target is a speaker embedding, not playback. */
    private fun resample(input: FloatArray, from: Int, to: Int): FloatArray {
        if (input.isEmpty()) return input
        val count = max((input.size.toDouble() * to / from).roundToInt(), 1)
        val out = FloatArray(count)
        val step = (input.size - 1).toDouble() / max(count - 1, 1)
        for (i in 0 until count) {
            val x = i * step
            val lo = x.toInt()
            val hi = min(lo + 1, input.size - 1)
            out[i] = (input[lo] + (input[hi] - input[lo]) * (x - lo)).toFloat()
        }
        return out
    }
}
