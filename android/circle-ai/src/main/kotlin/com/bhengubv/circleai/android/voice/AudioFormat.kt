// AudioFormat.kt
//
// Port of src/CircleAI.Voice/AudioFormat.cs.
//
// DECLARED HERE, unlike in the kotlin/ port. The sibling JVM port already
// carries AudioFormat in its own voice module, so the shared source deliberately
// does NOT declare it — but this android module had no voice module at all
// before the parity work, so there is nothing here to collide with.

package com.bhengubv.circleai.android.voice

/**
 * Describes a PCM audio format expected or produced by voice components.
 *
 * @param sampleRate Samples per second (e.g. 16000 for 16 kHz).
 * @param channels Number of interleaved channels (1 = mono, 2 = stereo).
 * @param bitsPerSample Bit depth of each sample (e.g. 16 for signed 16-bit PCM).
 */
data class AudioFormat(val sampleRate: Int, val channels: Int, val bitsPerSample: Int) {
    companion object {
        /**
         * Canonical input format expected by Butler / B! voice components:
         * PCM signed 16-bit, mono, 16 kHz. Most open-source ASR engines
         * (sherpa-onnx, Vosk) accept this directly.
         */
        val Pcm16Mono16k = AudioFormat(16_000, 1, 16)
    }
}
