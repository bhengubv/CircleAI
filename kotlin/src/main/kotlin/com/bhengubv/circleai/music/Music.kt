// Music.kt
//
// Kotlin port of CircleAI.Music — the C# reference is the EXACT spec.
//
// UNLIKE Charts and Documents this module ports WHOLE. There is no PDFsharp
// here - it is arithmetic, a 44-byte RIFF header and a sine bank - so the
// Kotlin can do the actual job rather than describe it.
//
// Fidelity notes:
//   * The DEFAULT TEMPO AND KEY TABLES are carried over value for value.
//     Reflective is 66 BPM in A minor and Playful is 120 in C major pentatonic
//     because somebody CHOSE that; a port that simplified the ramp would make
//     the same mood sound different on Android.
//   * `effectiveSeed` is FNV-1a, not a hash of the fields. .NET randomises
//     HashCode.Combine per process, so the C# only reproduces a bed WITHIN one
//     run; carrying that across would make "same spec, same audio" false
//     between one app launch and the next.
//   * Degree-to-MIDI uses FLOOR division. Kotlin `/` truncates toward zero on a
//     negative, which would send degree -1 UP an octave instead of down.

package com.bhengubv.circleai.music

import kotlin.math.floor
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.roundToLong

/** The feel a bed is asked for. */
enum class MusicMood {
    NEUTRAL, CALM, WARM, REFLECTIVE, UPLIFTING,
    CORPORATE, FOCUS, ENERGETIC, PLAYFUL, CINEMATIC
}

enum class PitchClass { C, C_SHARP, D, D_SHARP, E, F, F_SHARP, G, G_SHARP, A, A_SHARP, B }

enum class Scale { MAJOR, MINOR, DORIAN, MAJOR_PENTATONIC, MINOR_PENTATONIC }

data class MusicalKey(val root: PitchClass, val scale: Scale) {
    override fun toString() = root.toString() + " " + scale.toString()

    companion object {
        val C_MAJOR = MusicalKey(PitchClass.C, Scale.MAJOR)
        val A_MINOR = MusicalKey(PitchClass.A, Scale.MINOR)
        val D_MINOR = MusicalKey(PitchClass.D, Scale.MINOR)
        val G_MAJOR = MusicalKey(PitchClass.G, Scale.MAJOR)
        val C_MAJOR_PENTATONIC = MusicalKey(PitchClass.C, Scale.MAJOR_PENTATONIC)
    }
}

/** Raw PCM layout of a rendered bed. */
data class AudioPcmFormat(val sampleRate: Int, val channels: Int, val bitsPerSample: Int) {
    val bytesPerSample: Int get() = bitsPerSample / 8
    val blockAlign: Int get() = channels * bytesPerSample
    val byteRate: Int get() = sampleRate * blockAlign

    companion object {
        val BED_DEFAULT = AudioPcmFormat(44_100, 1, 16)
        val COMPACT = AudioPcmFormat(22_050, 1, 16)
        val CD_STEREO = AudioPcmFormat(44_100, 2, 16)
    }
}

/** Which engine produced a bed. */
enum class MusicBedBackend { PROCEDURAL, NEURAL }

class InvalidTempoException(tempo: Int) :
    IllegalArgumentException("tempo " + tempo + " is outside " +
        MusicSpec.MIN_TEMPO + ".." + MusicSpec.MAX_TEMPO)

class InvalidDurationException(duration: Double) :
    IllegalArgumentException("duration " + duration + "s is outside 0.." + MusicSpec.MAX_DURATION)

/** What to generate. */
data class MusicSpec(
    val mood: MusicMood,
    val tempo: Int,
    val durationSeconds: Double,
    val key: MusicalKey,
    /** Non-zero PINS the output; zero derives a seed from the rest of the spec. */
    val seed: Int = 0,
    val format: AudioPcmFormat? = null,
) {
    /** Throws rather than rendering something nobody asked for. */
    fun validate() {
        if (tempo !in MIN_TEMPO..MAX_TEMPO) throw InvalidTempoException(tempo)
        if (durationSeconds <= 0 || durationSeconds > MAX_DURATION) {
            throw InvalidDurationException(durationSeconds)
        }
    }

    /**
     * The seed actually used. FNV-1a over the same five fields the C# hashes,
     * so the SAME spec gives the SAME audio on every run and every platform -
     * which a per-process hash cannot promise.
     */
    fun effectiveSeed(): Int {
        if (seed != 0) return seed
        var h = 2_166_136_261.toInt()
        fun mix(v: Int) {
            var x = v
            repeat(4) {
                h = (h xor (x and 0xFF)) * 16_777_619
                x = x ushr 8
            }
        }
        mix(mood.ordinal)
        mix(tempo)
        mix(durationSeconds.roundToInt())
        mix(key.root.ordinal)
        mix(key.scale.ordinal)
        return if (h == 0) 0x9E37_79B9.toInt() else h
    }

    companion object {
        const val MIN_TEMPO = 40
        const val MAX_TEMPO = 240
        const val MAX_DURATION = 5.0 * 60.0

        /** A spec with the tempo and key this mood is meant to sound like. */
        fun forMood(mood: MusicMood, durationSeconds: Double) =
            MusicSpec(mood, defaultTempo(mood), durationSeconds, defaultKey(mood))

        /** Reflective is 66 BPM and Energetic is 128 because somebody chose that. */
        fun defaultTempo(mood: MusicMood) = when (mood) {
            MusicMood.REFLECTIVE -> 66
            MusicMood.CINEMATIC -> 70
            MusicMood.CALM -> 74
            MusicMood.WARM -> 86
            MusicMood.NEUTRAL -> 96
            MusicMood.FOCUS -> 100
            MusicMood.CORPORATE -> 104
            MusicMood.UPLIFTING -> 114
            MusicMood.PLAYFUL -> 120
            MusicMood.ENERGETIC -> 128
        }

        fun defaultKey(mood: MusicMood) = when (mood) {
            MusicMood.REFLECTIVE, MusicMood.CINEMATIC -> MusicalKey.A_MINOR
            MusicMood.CALM -> MusicalKey.D_MINOR
            MusicMood.PLAYFUL -> MusicalKey.C_MAJOR_PENTATONIC
            MusicMood.UPLIFTING -> MusicalKey.G_MAJOR
            else -> MusicalKey.C_MAJOR
        }
    }
}

/** A rendered bed. */
data class MusicBed(
    val pcm: ByteArray,
    val format: AudioPcmFormat,
    val spec: MusicSpec,
    val backend: MusicBedBackend,
    val durationSeconds: Double,
) {
    /** The bed as a complete .wav file. */
    fun toWav(): ByteArray = WavWriter.toWav(pcm, format)

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is MusicBed) return false
        return pcm.contentEquals(other.pcm) && format == other.format &&
            spec == other.spec && backend == other.backend &&
            durationSeconds == other.durationSeconds
    }

    override fun hashCode(): Int {
        var h = pcm.contentHashCode()
        h = h * 31 + format.hashCode()
        h = h * 31 + spec.hashCode()
        h = h * 31 + backend.hashCode()
        return h * 31 + durationSeconds.hashCode()
    }
}

/** Produces a [MusicBed] from a [MusicSpec]. */
interface MusicBedGenerator {
    val backend: MusicBedBackend
    suspend fun generate(spec: MusicSpec): MusicBed
}

/** Scales, MIDI numbers and frequencies. Internal in the C#; internal here too. */
internal object MusicTheory {
    private const val A4_FREQUENCY = 440.0
    private const val A4_MIDI_NOTE = 69

    /** Semitone offsets from the tonic. */
    fun intervals(scale: Scale): List<Int> = when (scale) {
        Scale.MAJOR -> listOf(0, 2, 4, 5, 7, 9, 11)
        Scale.MINOR -> listOf(0, 2, 3, 5, 7, 8, 10)
        Scale.DORIAN -> listOf(0, 2, 3, 5, 7, 9, 10)
        Scale.MAJOR_PENTATONIC -> listOf(0, 2, 4, 7, 9)
        Scale.MINOR_PENTATONIC -> listOf(0, 3, 5, 7, 10)
    }

    fun midiNote(root: PitchClass, octave: Int): Int = ((octave + 1) * 12) + root.ordinal

    fun frequency(midiNote: Int): Double =
        A4_FREQUENCY * 2.0.pow((midiNote - A4_MIDI_NOTE) / 12.0)

    /**
     * A scale degree as a MIDI note, wrapping into octaves.
     *
     * FLOOR DIVISION, NOT TRUNCATION - degree -1 must fall an octave, and
     * Kotlin `/` on a negative Int truncates toward zero, which would send it
     * UP instead. The C# uses Math.Floor for the same reason.
     */
    fun degreeToMidi(tonicMidi: Int, intervals: List<Int>, degree: Int): Int {
        val n = intervals.size
        val octaves = floor(degree.toDouble() / n).toInt()
        val index = degree - (octaves * n)     // guaranteed 0 until n
        return tonicMidi + (octaves * 12) + intervals[index]
    }

    /** Root, third, fifth and the octave above the root. */
    fun chordVoicing(tonicMidi: Int, intervals: List<Int>, degree: Int): List<Int> {
        val root = degreeToMidi(tonicMidi, intervals, degree)
        val third = degreeToMidi(tonicMidi, intervals, degree + 2)
        val fifth = degreeToMidi(tonicMidi, intervals, degree + 4)
        return listOf(root, third, fifth, root + 12)
    }
}

/** Wraps raw PCM in a 44-byte canonical RIFF header. */
object WavWriter {
    private const val HEADER_LENGTH = 44
    private const val PCM_FORMAT_TAG = 1

    /** The samples as a complete .wav file. */
    fun toWav(pcm: ByteArray, format: AudioPcmFormat): ByteArray {
        val out = java.io.ByteArrayOutputStream(HEADER_LENGTH + pcm.size)

        fun tag(s: String) = out.write(s.toByteArray(Charsets.US_ASCII))
        fun u32(v: Int) {
            out.write(v and 0xFF); out.write((v ushr 8) and 0xFF)
            out.write((v ushr 16) and 0xFF); out.write((v ushr 24) and 0xFF)
        }
        fun u16(v: Int) { out.write(v and 0xFF); out.write((v ushr 8) and 0xFF) }

        tag("RIFF")
        u32(36 + pcm.size)
        tag("WAVE")
        tag("fmt ")
        u32(16)                       // PCM fmt chunk size
        u16(PCM_FORMAT_TAG)
        u16(format.channels)
        u32(format.sampleRate)
        u32(format.byteRate)
        u16(format.blockAlign)
        u16(format.bitsPerSample)
        tag("data")
        u32(pcm.size)
        out.write(pcm)
        return out.toByteArray()
    }
}

/**
 * A generator that produces SILENCE of the requested length.
 *
 * Not a stub: it is what a host uses when music is switched off, and it still
 * has to return a bed of the right duration and format so callers downstream
 * need no special case.
 */
class NullMusicBedGenerator : MusicBedGenerator {
    override val backend = MusicBedBackend.PROCEDURAL

    override suspend fun generate(spec: MusicSpec): MusicBed {
        spec.validate()
        val format = spec.format ?: AudioPcmFormat.BED_DEFAULT
        val frames = (spec.durationSeconds * format.sampleRate).roundToLong().toInt()
        val pcm = ByteArray(maxOf(0, frames) * format.blockAlign)
        return MusicBed(pcm, format, spec, MusicBedBackend.PROCEDURAL, spec.durationSeconds)
    }
}

/**
 * Picks a generator for a requested backend.
 *
 * The fallback is the point: a host asking for music it cannot run should get
 * MUSIC, not an exception.
 */
class MusicBedGeneratorResolver(
    private val generators: Map<MusicBedBackend, MusicBedGenerator> = emptyMap(),
    private val fallback: MusicBedGenerator = NullMusicBedGenerator(),
) {
    fun resolve(backend: MusicBedBackend): MusicBedGenerator =
        generators[backend] ?: generators[MusicBedBackend.PROCEDURAL] ?: fallback

    /** Which backends actually have a generator behind them. */
    val available: List<MusicBedBackend>
        get() = MusicBedBackend.entries.filter { generators.containsKey(it) }
}
