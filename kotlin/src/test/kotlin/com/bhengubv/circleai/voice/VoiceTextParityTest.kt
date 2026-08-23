// VoiceTextParityTest.kt
//
// Asserts the Kotlin SentenceSplitter / LanguageSpanSplitter / GeezRomanizer /
// ToneShaper / NchltPhonemizer ports against the same golden files the C#
// reference generates.
//
// Every case in these fixtures is adversarial. The splitter fixture carries a
// decimal point and a domain name that must NOT split next to a danda and a CJK
// stop that must; the Ge'ez fixture carries the numerals that used to romanise
// as syllables; the tone fixture separates the biquad (bit-reproducible) from
// the coefficient derivation (pow/sin/cos, which no language guarantees to the
// last bit).

package com.bhengubv.circleai.voice

import kotlinx.serialization.json.*
import java.io.File
import kotlin.math.abs
import kotlin.math.max
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class VoiceTextParityTest {

    private val fixturesDir: File by lazy {
        // WALK UP UNTIL fixtures/ IS FOUND rather than counting directories:
        // Gradle's working directory is the project dir for a root project and
        // the SUBPROJECT dir for a module.
        generateSequence(File(System.getProperty("user.dir"))) { it.parentFile }
            .map { it.resolve("fixtures") }
            .firstOrNull { it.isDirectory }
            ?: error("no fixtures/ directory above ${System.getProperty("user.dir")}")
    }

    private fun readFixture(name: String): JsonObject =
        Json.parseToJsonElement(fixturesDir.resolve(name).readText(Charsets.UTF_8)).jsonObject

    private fun assertClose(got: Double, want: Double, tol: Double, what: String) {
        val scale = max(1.0, abs(want))
        assertTrue(abs(got - want) <= tol * scale, "$what: got $got, want $want (tol $tol)")
    }

    // ── SentenceSplitter ────────────────────────────────────────────────────

    @Test
    fun `sentence splitter matches reference`() {
        val fixture = readFixture("voice_sentence_splitter.json")
        assertEquals(
            fixture["maxCharsPerSegment"]!!.jsonPrimitive.int,
            SentenceSplitter.MAX_CHARS_PER_SEGMENT,
        )

        for (element in fixture["cases"]!!.jsonArray) {
            val c = element.jsonObject
            val name = c["name"]!!.jsonPrimitive.content
            val want = c["segments"]!!.jsonArray.map {
                SpeechSegment(
                    it.jsonObject["text"]!!.jsonPrimitive.content,
                    it.jsonObject["trailingPauseMs"]!!.jsonPrimitive.int,
                )
            }
            assertEquals(want, SentenceSplitter.split(c["text"]!!.jsonPrimitive.content), name)
        }
    }

    @Test
    fun `splits scripts that do not punctuate in Latin`() {
        // A Latin-only terminator list under-splits for about a billion people
        // and fails silently — the paragraph simply runs together.
        val cases = readFixture("voice_sentence_splitter.json")["cases"]!!.jsonArray
            .associate { it.jsonObject["name"]!!.jsonPrimitive.content to it.jsonObject }
        for (name in listOf("devanagari-danda", "urdu-full-stop", "cjk-no-space", "khmer-khan")) {
            val text = cases[name]!!["text"]!!.jsonPrimitive.content
            assertTrue(SentenceSplitter.split(text).size > 1, "$name must split")
        }
    }

    @Test
    fun `does not split a decimal point or a domain name`() {
        val cases = readFixture("voice_sentence_splitter.json")["cases"]!!.jsonArray
            .associate { it.jsonObject["name"]!!.jsonPrimitive.content to it.jsonObject }
        for (name in listOf("decimal-point", "domain-name")) {
            val text = cases[name]!!["text"]!!.jsonPrimitive.content
            assertEquals(2, SentenceSplitter.split(text).size, name)
        }
    }

    @Test
    fun `last segment carries no trailing pause`() {
        for (element in readFixture("voice_sentence_splitter.json")["cases"]!!.jsonArray) {
            val c = element.jsonObject
            val got = SentenceSplitter.split(c["text"]!!.jsonPrimitive.content)
            if (got.isNotEmpty()) {
                assertEquals(0, got.last().trailingPauseMs, c["name"]!!.jsonPrimitive.content)
            }
        }
    }

    // ── LanguageSpanSplitter ────────────────────────────────────────────────

    @Test
    fun `language spans match reference`() {
        val fixture = readFixture("voice_language_spans.json")

        for (element in fixture["split"]!!.jsonArray) {
            val c = element.jsonObject
            val text = c["text"]!!.jsonPrimitive.content
            val want = c["spans"]!!.jsonArray.map {
                LanguageSpan(
                    it.jsonObject["text"]!!.jsonPrimitive.content,
                    it.jsonObject["isForeign"]!!.jsonPrimitive.boolean,
                )
            }
            assertEquals(want, LanguageSpanSplitter.split(text), "spans for $text")
        }

        for (element in fixture["toSpokenForm"]!!.jsonArray) {
            val c = element.jsonObject
            val input = c["input"]!!.jsonPrimitive.content
            assertEquals(
                c["output"]!!.jsonPrimitive.content,
                LanguageSpanSplitter.toSpokenForm(input),
                "spoken form of $input",
            )
        }

        for (element in fixture["isForeignWord"]!!.jsonArray) {
            val c = element.jsonObject
            val word = c["word"]!!.jsonPrimitive.content
            assertEquals(
                c["foreign"]!!.jsonPrimitive.boolean,
                LanguageSpanSplitter.isForeignWord(word),
                "isForeignWord($word)",
            )
        }
    }

    @Test
    fun `an ordinary word is never flagged as foreign`() {
        // The conservatism is the contract, not an accident: guessing wrong
        // mispronounces a native word to fix a foreign one.
        assertTrue(!LanguageSpanSplitter.isForeignWord("hello"))
        assertTrue(!LanguageSpanSplitter.isForeignWord("Ngiyabonga"))
    }

    // ── GeezRomanizer ───────────────────────────────────────────────────────

    @Test
    fun `geez romanizer matches reference`() {
        val fixture = readFixture("voice_geez_romanizer.json")

        for (element in fixture["isEthiopic"]!!.jsonArray) {
            val c = element.jsonObject
            val text = c["text"]!!.jsonPrimitive.content
            assertEquals(
                c["ethiopic"]!!.jsonPrimitive.boolean,
                GeezRomanizer.isEthiopic(text),
                "isEthiopic($text)",
            )
        }

        for (element in fixture["romanize"]!!.jsonArray) {
            val c = element.jsonObject
            val input = c["input"]!!.jsonPrimitive.content
            assertEquals(
                c["output"]!!.jsonPrimitive.content,
                GeezRomanizer.romanize(input),
                "romanize($input)",
            )
        }
    }

    @Test
    fun `numerals are dropped rather than spoken`() {
        // The eight-per-consonant layout stops at U+1357. Sizing the range check
        // off the consonant table swept seven numerals back into the syllabary,
        // and they came out as sound, so nothing failed.
        assertEquals("", GeezRomanizer.romanize("፩፪፫"))
        assertEquals(
            "ryamyafya", GeezRomanizer.romanize("ፘፙፚ"),
            "the three LONE syllables are not a row of eight",
        )
    }

    // ── ToneShaper ──────────────────────────────────────────────────────────

    @Test
    fun `tone shaper uses the measured settings`() {
        // Field by field, and NOT against the whole fixture object: the shelf
        // slope is a private constant of the filter, not a settable value.
        val s = readFixture("voice_tone_shaper.json")["settings"]!!.jsonObject
        assertEquals(s["lowShelfHz"]!!.jsonPrimitive.double, ToneShaper.WARM.lowShelfHz)
        assertEquals(s["lowShelfDb"]!!.jsonPrimitive.double, ToneShaper.WARM.lowShelfDb)
        assertEquals(s["presenceHz"]!!.jsonPrimitive.double, ToneShaper.WARM.presenceHz)
        assertEquals(s["presenceDb"]!!.jsonPrimitive.double, ToneShaper.WARM.presenceDb)
        assertEquals(s["presenceQ"]!!.jsonPrimitive.double, ToneShaper.WARM.presenceQ)
        assertEquals(0.9, s["lowShelfSlope"]!!.jsonPrimitive.double)
    }

    @Test
    fun `tone shaper derives the same coefficients`() {
        // 1e-9 relative, not exact: pow, sin and cos are not bit-identical across
        // languages, and pretending otherwise makes a flaky test, not a strict one.
        val fixture = readFixture("voice_tone_shaper.json")
        val tol = fixture["coefficientTolerance"]!!.jsonPrimitive.double

        for (element in fixture["coefficients"]!!.jsonArray) {
            val c = element.jsonObject
            val rate = c["sampleRate"]!!.jsonPrimitive.int
            val got = mapOf(
                "lowShelf" to ToneShaper.lowShelf(ToneShaper.WARM, rate),
                "peaking" to ToneShaper.peaking(ToneShaper.WARM, rate),
            )
            for (name in listOf("lowShelf", "peaking")) {
                val want = c[name]!!.jsonObject
                for (i in 0 until 3) {
                    assertClose(
                        got[name]!!.b[i], want["b"]!!.jsonArray[i].jsonPrimitive.double,
                        tol, "$name b[$i] at $rate",
                    )
                    assertClose(
                        got[name]!!.a[i], want["a"]!!.jsonArray[i].jsonPrimitive.double,
                        tol, "$name a[$i] at $rate",
                    )
                }
            }
        }
    }

    @Test
    fun `tone shaper filters the fixture waveform identically`() {
        // The biquad is add and multiply on doubles, so THIS half is expected to
        // agree everywhere. Driving it from the fixture's own coefficients keeps
        // the transcendental functions out of the comparison.
        val fixture = readFixture("voice_tone_shaper.json")
        val w = fixture["waveform"]!!.jsonObject
        val rate = w["sampleRate"]!!.jsonPrimitive.int
        val coeffs = fixture["coefficients"]!!.jsonArray
            .map { it.jsonObject }
            .first { it["sampleRate"]!!.jsonPrimitive.int == rate }

        val x = w["input"]!!.jsonArray.map { it.jsonPrimitive.float }.toFloatArray()
        val before = x.maxOf { abs(it) }
        ToneShaper.biquad(x, coeffsOf(coeffs["lowShelf"]!!.jsonObject))
        ToneShaper.biquad(x, coeffsOf(coeffs["peaking"]!!.jsonObject))
        val after = x.maxOf { abs(it) }
        if (after > 0f && after > before) {
            val g = before / after
            for (i in x.indices) x[i] *= g
        }

        val want = w["output"]!!.jsonArray
        val tol = fixture["waveformTolerance"]!!.jsonPrimitive.double
        for (i in want.indices) {
            assertClose(x[i].toDouble(), want[i].jsonPrimitive.double, tol, "sample $i")
        }
    }

    @Test
    fun `silence stays silent rather than dividing by its peak`() {
        val fixture = readFixture("voice_tone_shaper.json")
        val want = fixture["silenceStaysSilent"]!!.jsonArray
        val silence = FloatArray(want.size)
        ToneShaper.apply(
            silence, fixture["waveform"]!!.jsonObject["sampleRate"]!!.jsonPrimitive.int,
        )
        for (i in want.indices) {
            assertEquals(want[i].jsonPrimitive.float, silence[i], "silence $i")
        }
    }

    @Test
    fun `both filters are applied not just one`() {
        // A port that dropped the presence dip would still change the waveform,
        // so "it moved" proves nothing — the two stages must differ.
        val fixture = readFixture("voice_tone_shaper.json")
        val w = fixture["waveform"]!!.jsonObject
        val rate = w["sampleRate"]!!.jsonPrimitive.int
        val input = w["input"]!!.jsonArray.map { it.jsonPrimitive.float }.toFloatArray()

        val both = input.copyOf()
        val onlyShelf = input.copyOf()
        ToneShaper.apply(both, rate)
        ToneShaper.biquad(onlyShelf, ToneShaper.lowShelf(ToneShaper.WARM, rate))

        assertTrue(
            both.indices.any { abs(both[it] - onlyShelf[it]) > 1e-4f },
            "the presence dip made no difference — it was not applied",
        )
    }

    private fun coeffsOf(o: JsonObject) = BiquadCoefficients(
        o["b"]!!.jsonArray.map { it.jsonPrimitive.double }.toDoubleArray(),
        o["a"]!!.jsonArray.map { it.jsonPrimitive.double }.toDoubleArray(),
    )

    // ── NchltPhonemizer ─────────────────────────────────────────────────────

    private fun makePhonemizer(fixture: JsonObject) = NchltPhonemizer.fromText(
        fixture["dict"]!!.jsonPrimitive.content,
        fixture["rules"]!!.jsonPrimitive.content,
        fixture["phoneMap"]!!.jsonPrimitive.content,
        fixture["graphMap"]!!.jsonPrimitive.content,
        fixture["gnulls"]!!.jsonPrimitive.content,
    )

    @Test
    fun `nchlt phonemizer matches reference`() {
        val fixture = readFixture("voice_nchlt_phonemizer.json")

        for (element in fixture["cases"]!!.jsonArray) {
            val c = element.jsonObject
            val name = c["name"]!!.jsonPrimitive.content
            val p = makePhonemizer(fixture)
            assertEquals(
                c["phones"]!!.jsonArray.map { it.jsonPrimitive.content },
                p.phonemize(c["text"]!!.jsonPrimitive.content),
                "phones for $name",
            )
            assertEquals(
                c["rulePredictedWords"]!!.jsonPrimitive.int, p.lastRulePredictedWords,
                "ruleWords for $name",
            )
            assertEquals(
                c["unknownGraphemes"]!!.jsonArray.map { it.jsonPrimitive.content },
                p.lastUnknownGraphemes, "unknown for $name",
            )
        }

        for (element in fixture["predictWord"]!!.jsonArray) {
            val c = element.jsonObject
            val word = c["word"]!!.jsonPrimitive.content
            assertEquals(
                c["phones"]!!.jsonArray.map { it.jsonPrimitive.content },
                makePhonemizer(fixture).predictWord(word),
                "predictWord($word)",
            )
        }
    }

    @Test
    fun `the dictionary beats the rules`() {
        // Both paths can pronounce this word. The dictionary must win, and the
        // rule counter must show it did — the counter is the only evidence of
        // which path ran, and a port that always predicted would still return
        // sensible phones.
        val p = makePhonemizer(readFixture("voice_nchlt_phonemizer.json"))
        p.phonemize("sawubona")
        assertEquals(0, p.lastRulePredictedWords, "a catalogued word must not be predicted")
    }

    @Test
    fun `an unknown grapheme is reported not guessed`() {
        val p = makePhonemizer(readFixture("voice_nchlt_phonemizer.json"))
        p.phonemize("azb")
        assertEquals(listOf("z"), p.lastUnknownGraphemes)
    }
}
