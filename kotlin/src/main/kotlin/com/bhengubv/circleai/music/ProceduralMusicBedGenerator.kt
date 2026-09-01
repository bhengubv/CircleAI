// ProceduralMusicBedGenerator.kt
//
// A music bed synthesised from nothing but arithmetic.
//
// WHY THIS EXISTS AT ALL. The alternative is a neural music model, which is
// hundreds of megabytes and does not run on the phone this is built for. This
// produces a listenable bed on any device, offline, in a fraction of real time,
// and it is the DEFAULT so a caller that asks for music always gets music.
//
// Two layers over a four-bar progression: a plucked arpeggio carrying the
// movement, and a slow triad pad an octave below carrying the harmony. In the
// same octave the two fight for the same frequencies and the result is muddy
// rather than full.
//
// DETERMINISTIC BY CONSTRUCTION. The only randomness is seeded velocity jitter,
// so the same spec produces byte-identical audio every time. That matters more
// than it sounds: a bed regenerated on a second device has to match, or a video
// assembled from one spec has different audio on every machine that renders it.
//
// Ported from src/CircleAI.Music/ProceduralMusicBedGenerator.cs.

package com.bhengubv.circleai.music

import kotlin.math.PI
import kotlin.math.exp
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.tanh

class ProceduralMusicBedGenerator {

    data class Voicing(
        val baseOctave: Int,
        val arpPerBeat: Int,
        /** Values index the four-note chord voicing [root, third, fifth, root+8ve]. */
        val arpPattern: IntArray,
        val harmonics: Int,
        val arpGain: Double,
        val padGain: Double
    )

    /**
     * Xorshift32, written out rather than taken from the standard library so the
     * SEQUENCE is identical on every platform: a seeded generator whose
     * algorithm can change between releases is not a seed, and the
     * byte-identical guarantee rests on it.
     */
    class XorShift(seed: Int) {
        // Zero is a fixed point of xorshift — it produces zero forever, so a
        // spec that seeded to 0 would render with no jitter at all.
        private var state: Int = if (seed == 0) 0x9E377_9B9.toInt() else seed

        fun nextUnit(): Double {
            state = state xor (state shl 13)
            state = state xor (state ushr 17)
            state = state xor (state shl 5)
            return (state.toLong() and 0xFFFFFFFFL).toDouble() / 0xFFFFFFFFL.toDouble()
        }
    }

    /**
     * Renders one mono buffer at [sampleRate], [seconds] long.
     *
     * The musical inputs are passed in rather than looked up, so this file holds
     * the SYNTHESIS and the catalogue of moods stays where moods are defined.
     */
    fun render(
        sampleRate: Int,
        seconds: Double,
        tempo: Int,
        tonicMidi: Int,
        intervals: IntArray,
        progression: IntArray,
        voicing: Voicing,
        seed: Int
    ): FloatArray {
        val frames = (seconds * sampleRate).roundToInt()
        val mono = FloatArray(max(0, frames))
        if (mono.isEmpty()) return mono

        val secondsPerBeat = 60.0 / tempo
        val secondsPerBar = secondsPerBeat * BEATS_PER_BAR
        val arpNoteSeconds = secondsPerBeat / voicing.arpPerBeat
        val rng = XorShift(seed)

        renderArpeggio(
            mono, sampleRate, seconds, secondsPerBar, arpNoteSeconds,
            tonicMidi, intervals, progression, voicing, rng
        )
        // The pad sits an OCTAVE BELOW the arpeggio.
        renderPad(
            mono, sampleRate, seconds, secondsPerBar,
            tonicMidi - 12, intervals, progression, voicing.padGain
        )
        applyMaster(mono, sampleRate)
        return mono
    }

    private fun renderArpeggio(
        buffer: FloatArray, sampleRate: Int, totalSeconds: Double, secondsPerBar: Double,
        arpNoteSeconds: Double, tonicMidi: Int, intervals: IntArray, progression: IntArray,
        voicing: Voicing, rng: XorShift
    ) {
        if (arpNoteSeconds <= 0.0 || secondsPerBar <= 0.0) return

        val totalNotes = Math.ceil(totalSeconds / arpNoteSeconds).toInt()
        val noteSamples = max(1, (arpNoteSeconds * sampleRate).toInt())

        for (noteIndex in 0 until totalNotes) {
            val noteStart = noteIndex * arpNoteSeconds
            val bar = (noteStart / secondsPerBar).toInt()
            val degree = progression[bar % progression.size]
            val chord = chordVoicing(tonicMidi, intervals, degree)
            val tone = voicing.arpPattern[noteIndex % voicing.arpPattern.size]

            // Deterministic velocity jitter so repeated notes do not sound
            // robotic. Seeded, so the same spec still renders identically.
            val velocity = 0.85 + 0.30 * rng.nextUnit()
            renderPluck(
                buffer, (noteStart * sampleRate).toInt(), noteSamples, sampleRate,
                frequency(chord[tone]), voicing.arpGain * velocity, voicing.harmonics
            )
        }
    }

    private fun renderPluck(
        buffer: FloatArray, start: Int, length: Int, sampleRate: Int,
        frequency: Double, gain: Double, harmonics: Int
    ) {
        if (length <= 0 || start >= buffer.size) return

        val attackSamples = max(1.0, min(0.006 * sampleRate, length * 0.25))
        val decayK = 4.5 / length                 // ~e^-4.5 by the end of the note
        val phaseInc = PI2 * frequency / sampleRate
        // Normalised so adding harmonics makes a note BRIGHTER, not louder —
        // without this the energetic moods clip and the calm ones do not.
        val norm = if (harmonics >= 3) 1.53 else if (harmonics >= 2) 1.35 else 1.0

        for (i in 0 until length) {
            val index = start + i
            if (index >= buffer.size) break

            val envelope = if (i < attackSamples) i / attackSamples
            else exp(-decayK * (i - attackSamples))

            val phase = phaseInc * i
            var sample = sin(phase)
            if (harmonics >= 2) sample += 0.35 * sin(2.0 * phase)
            if (harmonics >= 3) sample += 0.18 * sin(3.0 * phase)

            buffer[index] += (envelope * gain * (sample / norm)).toFloat()
        }
    }

    private fun renderPad(
        buffer: FloatArray, sampleRate: Int, totalSeconds: Double, secondsPerBar: Double,
        padTonicMidi: Int, intervals: IntArray, progression: IntArray, gain: Double
    ) {
        if (secondsPerBar <= 0.0 || gain <= 0.0) return

        val totalBars = Math.ceil(totalSeconds / secondsPerBar).toInt()
        val barSamples = max(1, (secondsPerBar * sampleRate).toInt())

        for (bar in 0 until totalBars) {
            val degree = progression[bar % progression.size]
            val chord = chordVoicing(padTonicMidi, intervals, degree)
            renderPadChord(
                buffer, (bar * secondsPerBar * sampleRate).toInt(),
                barSamples, sampleRate, chord, gain
            )
        }
    }

    private fun renderPadChord(
        buffer: FloatArray, start: Int, length: Int, sampleRate: Int,
        chord: IntArray, gain: Double
    ) {
        if (length <= 0 || start >= buffer.size) return

        // A TRIAD only. The fourth voice is the octave, and doubling it in a
        // sustained pad is what makes a bed sound like an organ.
        val voices = min(3, chord.size)
        if (voices <= 0) return

        val phaseInc = DoubleArray(voices) { PI2 * frequency(chord[it]) / sampleRate }
        val attack = length * 0.15
        val release = length * 0.15
        val releaseStart = length - release
        val voiceScale = 1.0 / voices

        for (i in 0 until length) {
            val index = start + i
            if (index >= buffer.size) break

            val d = i.toDouble()
            val envelope = when {
                d < attack -> d / attack
                d > releaseStart -> (length - d) / release
                else -> 1.0
            }

            var sample = 0.0
            for (v in 0 until voices) sample += sin(phaseInc[v] * d)
            buffer[index] += (envelope * gain * voiceScale * sample).toFloat()
        }
    }

    private fun applyMaster(buffer: FloatArray, sampleRate: Int) {
        if (buffer.isEmpty()) return

        // SOFT limit, not a hard clip. Two layers summing past full scale is
        // normal here, and hard clipping turns that into audible crackle where
        // tanh just squashes it.
        for (i in buffer.indices) buffer[i] = tanh(buffer[i].toDouble()).toFloat()

        // Fades at both ends. A bed that starts at full amplitude begins with a
        // click, and every listener hears the click.
        val fadeIn = min((0.03 * sampleRate).toInt(), buffer.size / 2)
        val fadeOut = min((0.05 * sampleRate).toInt(), buffer.size / 2)
        for (i in 0 until fadeIn) buffer[i] = (buffer[i] * (i.toDouble() / fadeIn)).toFloat()
        for (i in 0 until fadeOut) {
            buffer[buffer.size - 1 - i] = (buffer[buffer.size - 1 - i] * (i.toDouble() / fadeOut)).toFloat()
        }
    }

    /** 0.9 is extra headroom on top of the limiter, so a bed mixed under a voice
     *  track has somewhere to go. */
    fun toPcm16(mono: FloatArray, channels: Int): ByteArray {
        val pcm = ByteArray(mono.size * channels * 2)
        var p = 0
        for (sample in mono) {
            val scaled = sample * 32767.0 * 0.9
            val value = max(-32768.0, min(32767.0, scaled)).toInt().toShort()
            val lo = (value.toInt() and 0xFF).toByte()
            val hi = ((value.toInt() shr 8) and 0xFF).toByte()
            repeat(channels) { pcm[p++] = lo; pcm[p++] = hi }   // little-endian
        }
        return pcm
    }

    companion object {
        private const val BEATS_PER_BAR = 4
        private const val PI2 = 2.0 * PI

        /**
         * Bright scales get I–V–vi–IV; dark ones get i–VI–III–VII.
         *
         * Degrees are 0-based scale steps and the voicing wraps octaves, which is
         * the only reason one table serves pentatonic and diatonic alike.
         */
        fun progressionFor(bright: Boolean): IntArray =
            if (bright) intArrayOf(0, 4, 5, 3) else intArrayOf(0, 5, 2, 6)

        fun frequency(midiNote: Int): Double = 440.0 * Math.pow(2.0, (midiNote - 69) / 12.0)

        fun chordVoicing(tonicMidi: Int, intervals: IntArray, degree: Int): IntArray {
            fun step(n: Int): Int {
                val octaves = Math.floorDiv(n, intervals.size)
                return tonicMidi + intervals[Math.floorMod(n, intervals.size)] + 12 * octaves
            }
            return intArrayOf(step(degree), step(degree + 2), step(degree + 4), step(degree) + 12)
        }
    }
}
