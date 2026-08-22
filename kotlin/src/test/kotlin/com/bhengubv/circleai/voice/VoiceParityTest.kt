// VoiceParityTest.kt
//
// Asserts the Kotlin voice port against the SAME golden files the C# reference
// generates (tools/voice-fixtures). Not "does Kotlin do something sensible" —
// "does Kotlin produce identical answers to every other port".
//
// The fixtures are adversarial on purpose: the SentencePiece vocabulary is built
// so greedy longest-match and Viterbi DISAGREE, and the X-SAMPA cases carry a
// multi-character token, the script-g that is U+0261 rather than ASCII 'g', and
// a phone that cannot map and must be REPORTED rather than dropped.

package com.bhengubv.circleai.voice

import kotlinx.serialization.json.*
import java.io.File
import java.util.Base64
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue

class VoiceParityTest {

    private val fixturesDir: File by lazy {
        // WALK UP UNTIL fixtures/ IS FOUND rather than counting directories.
        // Gradle sets the working directory to the project dir for a root
        // project and to the SUBPROJECT dir for a module, so a fixed number of
        // parentFile hops is right in one layout and silently wrong in the
        // other — and "file not found" reads like a missing fixture rather than
        // a miscounted path.
        generateSequence(File(System.getProperty("user.dir"))) { it.parentFile }
            .map { it.resolve("fixtures") }
            .firstOrNull { it.isDirectory }
            ?: error("no fixtures/ directory above ${System.getProperty("user.dir")}")
    }

    private fun readFixture(name: String): JsonObject =
        Json.parseToJsonElement(fixturesDir.resolve(name).readText(Charsets.UTF_8)).jsonObject

    // ── X-SAMPA → IPA ───────────────────────────────────────────────────────

    @Test
    fun `xsampa to ipa matches reference`() {
        val fixture = readFixture("voice_xsampa_to_ipa.json")
        val cases = fixture["cases"]!!.jsonArray
        assertTrue(cases.isNotEmpty(), "fixture has no cases")

        for (element in cases) {
            val case = element.jsonObject
            val xsampa = case["xsampa"]!!.jsonArray.map { it.jsonPrimitive.content }
            val expectedIpa = case["ipa"]!!.jsonArray.map { it.jsonPrimitive.content }
            val expectedUnmapped = case["unmapped"]!!.jsonArray.map { it.jsonPrimitive.content }
            val expectedCanSayAll = case["canSayAll"]!!.jsonPrimitive.boolean

            val result = XsampaToIpa.convert(xsampa)
            assertEquals(expectedIpa, result.ipa, "ipa for $xsampa")
            assertEquals(expectedUnmapped, result.unmapped, "unmapped for $xsampa")
            assertEquals(expectedCanSayAll, XsampaToIpa.canSayAll(xsampa), "canSayAll for $xsampa")
        }
    }

    @Test
    fun `xsampa known phones match reference`() {
        val fixture = readFixture("voice_xsampa_to_ipa.json")
        val expected = fixture["knownPhones"]!!.jsonArray.map { it.jsonPrimitive.content }.toSet()
        assertEquals(
            expected,
            XsampaToIpa.knownPhones().toSet(),
            "the phone table itself has drifted from the reference",
        )
    }

    @Test
    fun `script g is U0261 not ascii g`() {
        // Called out on its own because it is invisible in a diff: the voice's
        // vocabulary carries ɡ (U+0261) and a plain ASCII 'g' silently misses.
        val ipa = XsampaToIpa.convert(listOf("g")).ipa
        assertEquals(listOf("ɡ"), ipa)
        assertNotEquals(listOf("g"), ipa, "ASCII g would be dropped by the voice")
    }

    // ── SentencePiece unigram ───────────────────────────────────────────────

    private fun loadSp(): Pair<SentencePieceUnigram, JsonObject> {
        val fixture = readFixture("voice_sentencepiece_unigram.json")
        val vocab = fixture["vocab"]!!.jsonObject.mapValues { it.value.jsonPrimitive.int }
        val scores = fixture["scores"]!!.jsonObject.mapValues { it.value.jsonPrimitive.float }
        return SentencePieceUnigram(vocab, scores) to fixture
    }

    @Test
    fun `sentencepiece matches reference`() {
        val (sp, fixture) = loadSp()
        val cases = fixture["cases"]!!.jsonArray
        assertTrue(cases.isNotEmpty(), "fixture has no cases")

        for (element in cases) {
            val case = element.jsonObject
            val text = case["text"]!!.jsonPrimitive.content
            val expected = case["ids"]!!.jsonArray.map { it.jsonPrimitive.int }
            assertEquals(expected, sp.encode(text), "ids for \"$text\"")
        }
    }

    @Test
    fun `viterbi not greedy`() {
        // The fixture vocabulary is built so the two disagree: "▁hello" scores
        // WORSE than "▁hell" + "o". Greedy picks the long piece; Viterbi does
        // not. Without this, a greedy port looks correct.
        val (sp, fixture) = loadSp()
        val vocab = fixture["vocab"]!!.jsonObject.mapValues { it.value.jsonPrimitive.int }
        val want = listOf(vocab["▁hell"]!!, vocab["o"]!!, vocab["▁world"]!!)
        val greedy = listOf(vocab["▁hello"]!!, vocab["▁world"]!!)

        val got = sp.encode("hello world")
        assertEquals(want, got)
        assertNotEquals(greedy, got, "this is the greedy answer — the port is not doing Viterbi")
    }

    @Test
    fun `byte fallback keeps utf8 order`() {
        // é is UTF-8 C3 A9. Emitting A9 C3 does not throw — both are real pieces
        // with real ids — the model just says a different character, and only
        // outside ASCII, which is exactly the languages this catalogue serves.
        val (sp, fixture) = loadSp()
        val vocab = fixture["vocab"]!!.jsonObject.mapValues { it.value.jsonPrimitive.int }
        val got = sp.encode("hé")
        assertTrue(got.size >= 2, "expected byte fallback pieces, got $got")
        assertEquals(
            listOf(vocab["<0xC3>"]!!, vocab["<0xA9>"]!!),
            got.takeLast(2),
            "byte fallback emitted UTF-8 bytes in the wrong order",
        )
    }

    @Test
    fun `empty text encodes to nothing`() {
        val (sp, _) = loadSp()
        assertEquals(emptyList(), sp.encode(""))
    }

    // ── WAV I/O ─────────────────────────────────────────────────────────────

    private fun wavCases(): List<JsonObject> =
        readFixture("voice_wav_io.json")["cases"]!!.jsonArray.map { it.jsonObject }

    private fun decode(case: JsonObject): Wav =
        WavIo.parse(Base64.getDecoder().decode(case["wavBase64"]!!.jsonPrimitive.content))

    @Test
    fun `wav io matches reference`() {
        val cases = wavCases()
        assertTrue(cases.isNotEmpty(), "fixture has no cases")

        for (case in cases) {
            val name = case["name"]!!.jsonPrimitive.content
            val expected = case["expected"]!!.jsonObject
            val wantCount = expected["sampleCount"]!!.jsonPrimitive.int
            val wantSamples = expected["samples"]!!.jsonArray.map { it.jsonPrimitive.float }

            val mono = WavIo.toMono24k(decode(case))
            assertEquals(wantCount, mono.size, "sampleCount for $name")
            for ((i, want) in wantSamples.withIndex()) {
                assertTrue(
                    kotlin.math.abs(mono[i] - want) < 1e-6f,
                    "sample $i of $name: got ${mono[i]}, want $want",
                )
            }
        }
    }

    @Test
    fun `wav io walks chunks rather than assuming byte 44`() {
        // The LIST-chunk case is the one that matters: a reader that assumes data
        // starts at byte 44 reads metadata as audio.
        val cases = wavCases()
        val plain = cases.first { it["name"]!!.jsonPrimitive.content.contains("plain") }
        val listed = cases.first { it["name"]!!.jsonPrimitive.content.contains("LIST") }
        assertTrue(
            decode(plain).samples.contentEquals(decode(listed).samples),
            "a LIST chunk before the data changed the decoded audio",
        )
    }
}
