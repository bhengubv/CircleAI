package com.bhengubv.circleai.music

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotEquals

/** The spec: moods, tempos and the derived seed. */
class MusicSpecTest {

    // Reflective is 66 BPM in A minor because somebody CHOSE that; a port that
    // simplified the ramp would make the same mood sound different on Android.
    @Test fun `each mood keeps its chosen tempo and key`() {
        assertEquals(66, MusicSpec.defaultTempo(MusicMood.REFLECTIVE))
        assertEquals(128, MusicSpec.defaultTempo(MusicMood.ENERGETIC))
        assertEquals(120, MusicSpec.defaultTempo(MusicMood.PLAYFUL))
        assertEquals(MusicalKey.A_MINOR, MusicSpec.defaultKey(MusicMood.REFLECTIVE))
        assertEquals(MusicalKey.C_MAJOR_PENTATONIC, MusicSpec.defaultKey(MusicMood.PLAYFUL))
        assertEquals(MusicalKey.C_MAJOR, MusicSpec.defaultKey(MusicMood.NEUTRAL))
    }

    @Test fun `the tempo ramp is monotonic from reflective to energetic`() {
        val order = listOf(
            MusicMood.REFLECTIVE, MusicMood.CINEMATIC, MusicMood.CALM, MusicMood.WARM,
            MusicMood.NEUTRAL, MusicMood.FOCUS, MusicMood.CORPORATE, MusicMood.UPLIFTING,
            MusicMood.PLAYFUL, MusicMood.ENERGETIC,
        )
        val tempos = order.map { MusicSpec.defaultTempo(it) }
        assertEquals(tempos.sorted(), tempos, "the ramp must never go backwards")
    }

    @Test fun `a spec for a mood carries that mood defaults`() {
        val s = MusicSpec.forMood(MusicMood.CALM, 30.0)
        assertEquals(74, s.tempo)
        assertEquals(MusicalKey.D_MINOR, s.key)
        assertEquals(30.0, s.durationSeconds)
    }

    // Throws rather than rendering something nobody asked for.
    @Test fun `an impossible tempo or duration is refused`() {
        assertFailsWith<InvalidTempoException> {
            MusicSpec(MusicMood.CALM, 10, 30.0, MusicalKey.C_MAJOR).validate()
        }
        assertFailsWith<InvalidTempoException> {
            MusicSpec(MusicMood.CALM, 500, 30.0, MusicalKey.C_MAJOR).validate()
        }
        assertFailsWith<InvalidDurationException> {
            MusicSpec(MusicMood.CALM, 100, 0.0, MusicalKey.C_MAJOR).validate()
        }
        assertFailsWith<InvalidDurationException> {
            MusicSpec(MusicMood.CALM, 100, 3600.0, MusicalKey.C_MAJOR).validate()
        }
    }

    @Test fun `the tempo bounds themselves are valid`() {
        MusicSpec(MusicMood.CALM, MusicSpec.MIN_TEMPO, 1.0, MusicalKey.C_MAJOR).validate()
        MusicSpec(MusicMood.CALM, MusicSpec.MAX_TEMPO, MusicSpec.MAX_DURATION, MusicalKey.C_MAJOR).validate()
    }

    // Same spec, same audio - on every run and every platform.
    @Test fun `the derived seed is stable and depends on the whole spec`() {
        val a = MusicSpec(MusicMood.CALM, 90, 30.0, MusicalKey.C_MAJOR)
        assertEquals(a.effectiveSeed(), a.effectiveSeed())
        assertEquals(a.effectiveSeed(), a.copy().effectiveSeed())

        assertNotEquals(a.effectiveSeed(), a.copy(tempo = 91).effectiveSeed())
        assertNotEquals(a.effectiveSeed(), a.copy(mood = MusicMood.WARM).effectiveSeed())
        assertNotEquals(a.effectiveSeed(), a.copy(key = MusicalKey.A_MINOR).effectiveSeed())
        assertNotEquals(a.effectiveSeed(), a.copy(durationSeconds = 31.0).effectiveSeed())
    }

    // A non-zero seed PINS the output regardless of the rest.
    @Test fun `an explicit seed is used as-is`() {
        val s = MusicSpec(MusicMood.CALM, 90, 30.0, MusicalKey.C_MAJOR, seed = 42)
        assertEquals(42, s.effectiveSeed())
        assertEquals(42, s.copy(tempo = 200).effectiveSeed())
    }
}
