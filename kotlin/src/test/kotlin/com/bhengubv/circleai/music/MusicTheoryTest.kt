package com.bhengubv.circleai.music

import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

/** The theory, the WAV header and the generators. */
class MusicTheoryTest {

    @Test fun `each scale has its published intervals`() {
        assertEquals(listOf(0, 2, 4, 5, 7, 9, 11), MusicTheory.intervals(Scale.MAJOR))
        assertEquals(listOf(0, 2, 3, 5, 7, 8, 10), MusicTheory.intervals(Scale.MINOR))
        assertEquals(5, MusicTheory.intervals(Scale.MAJOR_PENTATONIC).size)
    }

    // A4 is MIDI 69 at 440 Hz; middle C is 60.
    @Test fun `midi numbers and frequencies line up with the standard`() {
        assertEquals(69, MusicTheory.midiNote(PitchClass.A, 4))
        assertEquals(60, MusicTheory.midiNote(PitchClass.C, 4))
        assertEquals(440.0, MusicTheory.frequency(69), 1e-9)
        assertEquals(880.0, MusicTheory.frequency(81), 1e-9)
        assertEquals(220.0, MusicTheory.frequency(57), 1e-9)
    }

    @Test fun `a degree inside the scale is the interval above the tonic`() {
        val maj = MusicTheory.intervals(Scale.MAJOR)
        assertEquals(60, MusicTheory.degreeToMidi(60, maj, 0))
        assertEquals(64, MusicTheory.degreeToMidi(60, maj, 2))
        assertEquals(71, MusicTheory.degreeToMidi(60, maj, 6))
    }

    @Test fun `a degree past the scale wraps up an octave`() {
        val maj = MusicTheory.intervals(Scale.MAJOR)
        // Degree 7 in a seven-note scale is the tonic an octave up...
        assertEquals(72, MusicTheory.degreeToMidi(60, maj, 7))
        // ...and degree 9 is an octave up plus degree 2, which is a major third.
        assertEquals(76, MusicTheory.degreeToMidi(60, maj, 9))
    }

    // FLOOR division: degree -1 must FALL an octave. Truncation sends it UP.
    @Test fun `a negative degree falls rather than rising`() {
        val maj = MusicTheory.intervals(Scale.MAJOR)
        assertEquals(59, MusicTheory.degreeToMidi(60, maj, -1), "one below the tonic is B")
        assertEquals(48, MusicTheory.degreeToMidi(60, maj, -7), "a whole octave down")
    }

    @Test fun `a chord voicing is root third fifth and the octave`() {
        val maj = MusicTheory.intervals(Scale.MAJOR)
        assertEquals(listOf(60, 64, 67, 72), MusicTheory.chordVoicing(60, maj, 0))
    }

    @Test fun `the format arithmetic matches the wav fields`() {
        val f = AudioPcmFormat.CD_STEREO
        assertEquals(2, f.bytesPerSample)
        assertEquals(4, f.blockAlign)
        assertEquals(176_400, f.byteRate)
    }

    // A 44-byte canonical header, little-endian, with the sizes a player checks.
    @Test fun `the wav header is canonical`() {
        val wav = WavWriter.toWav(ByteArray(100), AudioPcmFormat.BED_DEFAULT)
        assertEquals(144, wav.size, "44-byte header plus the samples")
        assertEquals("RIFF", String(wav, 0, 4))
        assertEquals("WAVE", String(wav, 8, 4))
        assertEquals("fmt ", String(wav, 12, 4))
        assertEquals("data", String(wav, 36, 4))

        fun u32(at: Int) = (wav[at].toInt() and 0xFF) or ((wav[at + 1].toInt() and 0xFF) shl 8) or
            ((wav[at + 2].toInt() and 0xFF) shl 16) or ((wav[at + 3].toInt() and 0xFF) shl 24)
        assertEquals(136, u32(4), "RIFF size is everything after the first 8 bytes")
        assertEquals(16, u32(16), "PCM fmt chunk size")
        assertEquals(44_100, u32(24))
        assertEquals(100, u32(40), "data size is the payload")
    }

    // Not a stub: silence of the RIGHT length, so callers need no special case.
    @Test fun `the null generator returns silence of the right length`() = runTest {
        val bed = NullMusicBedGenerator().generate(MusicSpec.forMood(MusicMood.CALM, 2.0))
        assertEquals(2.0, bed.durationSeconds)
        assertEquals(MusicBedBackend.PROCEDURAL, bed.backend)
        assertEquals(44_100 * 2 * 2, bed.pcm.size, "2 s of 16-bit mono at 44.1 kHz")
        assertTrue(bed.pcm.all { it.toInt() == 0 })
    }

    @Test fun `the null generator still validates the spec`() = runTest {
        assertFailsWith<InvalidTempoException> {
            NullMusicBedGenerator().generate(MusicSpec(MusicMood.CALM, 5, 1.0, MusicalKey.C_MAJOR))
        }
    }

    @Test fun `a bed can be wrapped as a wav`() = runTest {
        val bed = NullMusicBedGenerator().generate(MusicSpec.forMood(MusicMood.CALM, 1.0))
        assertEquals(bed.pcm.size + 44, bed.toWav().size)
    }

    // A host asking for music it cannot run should get MUSIC, not an exception.
    @Test fun `the resolver falls back rather than failing`() {
        val r = MusicBedGeneratorResolver()
        assertTrue(r.resolve(MusicBedBackend.NEURAL) is NullMusicBedGenerator)
        assertTrue(r.available.isEmpty())
    }

    @Test fun `the resolver prefers a registered generator`() {
        val procedural = NullMusicBedGenerator()
        val r = MusicBedGeneratorResolver(mapOf(MusicBedBackend.PROCEDURAL to procedural))
        assertEquals(procedural, r.resolve(MusicBedBackend.PROCEDURAL))
        assertEquals(procedural, r.resolve(MusicBedBackend.NEURAL), "neural falls back to procedural")
        assertEquals(listOf(MusicBedBackend.PROCEDURAL), r.available)
    }
}
