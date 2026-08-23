// VoicePiperParityTest.kt
//
// Asserts the Kotlin PiperVoiceConfig / LexiconTokeniser / AudioFormat ports
// against the same golden files the C# reference generates.
//
// The piper fixture carries TWO configs on purpose — one with pad 0 and one with
// pad 3 — so a port that hard-codes either fails on the other. That is THE PAD
// RULE, and getting it wrong is what made 42 MMS voices speak fluent nonsense.

package com.bhengubv.circleai.android.voice

import kotlinx.serialization.json.*
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class VoicePiperParityTest {

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

    private fun mapOfIds(o: JsonObject): Map<String, List<Long>> =
        o.mapValues { (_, v) -> v.jsonArray.map { it.jsonPrimitive.long } }

    // ── PiperVoiceConfig ────────────────────────────────────────────────────

    @Test
    fun `piper config matches reference`() {
        val configs = readFixture("voice_piper_config.json")["configs"]!!.jsonArray
        assertEquals(2, configs.size, "both pad conventions must be covered")

        for (element in configs) {
            val c = element.jsonObject
            val name = c["name"]!!.jsonPrimitive.content
            val cfg = PiperVoiceConfig(
                mapOfIds(c["configJson"]!!.jsonObject),
                sampleRate = c["sampleRate"]!!.jsonPrimitive.int,
            )

            assertEquals(c["padId"]!!.jsonPrimitive.long, cfg.padId, "padId for $name")
            assertEquals(
                c["hasPhonemeMap"]!!.jsonPrimitive.boolean, cfg.hasPhonemeMap,
                "hasPhonemeMap for $name",
            )

            for (caseElement in c["cases"]!!.jsonArray) {
                val one = caseElement.jsonObject
                val phonemes = one["phonemes"]!!.jsonArray.map { it.jsonPrimitive.content }
                val got = cfg.phonemesToIds(phonemes)

                assertEquals(
                    one["ids"]!!.jsonArray.map { it.jsonPrimitive.long }, got.ids,
                    "ids for $phonemes in $name",
                )
                assertEquals(one["skipped"]!!.jsonPrimitive.int, got.skipped, "skipped for $phonemes")
                assertEquals(
                    one["skippedSymbols"]!!.jsonArray.map { it.jsonPrimitive.content },
                    got.skippedSymbols, "skippedSymbols for $phonemes",
                )
                assertEquals(
                    one["approximatedSymbols"]!!.jsonArray.map { it.jsonPrimitive.content },
                    got.approximatedSymbols, "approximatedSymbols for $phonemes",
                )
            }
        }
    }

    @Test
    fun `pad is read from the model not assumed`() {
        // THE PAD RULE. The two fixture configs disagree — 0 in the Piper-layout
        // one, 3 in the MMS-layout one — so a port that hard-codes either fails.
        val configs = readFixture("voice_piper_config.json")["configs"]!!.jsonArray
        val pads = configs.map { it.jsonObject["padId"]!!.jsonPrimitive.long }.toSet()
        assertEquals(setOf(0L, 3L), pads, "the fixture must cover BOTH pad conventions")

        for (element in configs) {
            val c = element.jsonObject
            assertEquals(
                c["padId"]!!.jsonPrimitive.long,
                PiperVoiceConfig(mapOfIds(c["configJson"]!!.jsonObject)).padId,
            )
        }
    }

    @Test
    fun `thai is not folded but tshivenda is`() {
        // The asymmetry is the whole point. Latin ṱ still sounds like a t with
        // the mark gone; Thai ก's marks ARE the vowels, so folding deletes it.
        val configs = readFixture("voice_piper_config.json")["configs"]!!.jsonArray
        val cfg = PiperVoiceConfig(mapOfIds(configs[0].jsonObject["configJson"]!!.jsonObject))

        assertEquals(
            listOf("ṱ"), cfg.phonemesToIds(listOf("ṱ")).approximatedSymbols,
            "ṱ should fold to a Latin base and be REPORTED as approximate",
        )
        assertEquals(
            listOf("ก"), cfg.phonemesToIds(listOf("ก")).skippedSymbols,
            "Thai must be skipped, not folded",
        )
    }

    @Test
    fun `split phoneme string matches reference`() {
        for (element in readFixture("voice_piper_config.json")["splitPhonemeString"]!!.jsonArray) {
            val c = element.jsonObject
            val input = c["input"]!!.jsonPrimitive.content
            assertEquals(
                c["elements"]!!.jsonArray.map { it.jsonPrimitive.content },
                PiperVoiceConfig.splitPhonemeString(input),
                "clusters for $input",
            )
        }
    }

    // ── LexiconTokeniser ────────────────────────────────────────────────────

    private fun makeLexicon(): Pair<LexiconTokeniser, JsonObject> {
        val fixture = readFixture("voice_lexicon_tokeniser.json")
        val newline = "\n"
        val tokensText = fixture["tokens"]!!.jsonObject.entries.joinToString(newline) {
            "${it.key} ${it.value.jsonPrimitive.long}"
        }
        val lexiconText = fixture["lexicon"]!!.jsonArray.joinToString(newline) { e ->
            val o = e.jsonObject
            o["word"]!!.jsonPrimitive.content + " " +
                o["phonemes"]!!.jsonArray.joinToString(" ") { it.jsonPrimitive.content }
        }
        val lex = LexiconTokeniser.fromText(
            tokensText, lexiconText, fixture["blank"]!!.jsonPrimitive.long,
        )
        assertTrue(lex != null, "fixture lexicon failed to load")
        return lex!! to fixture
    }

    @Test
    fun `lexicon tokeniser matches reference`() {
        val (lex, fixture) = makeLexicon()
        val cases = fixture["cases"]!!.jsonArray
        assertTrue(cases.isNotEmpty(), "fixture has no cases")

        for (element in cases) {
            val c = element.jsonObject
            val text = c["text"]!!.jsonPrimitive.content
            assertEquals(
                c["ids"]!!.jsonArray.map { it.jsonPrimitive.long },
                lex.encode(text, interleaveBlank = false), "ids for $text",
            )
            assertEquals(
                c["unmapped"]!!.jsonArray.map { it.jsonPrimitive.content },
                lex.lastUnmapped, "unmapped for $text",
            )
            assertEquals(
                c["idsWithBlank"]!!.jsonArray.map { it.jsonPrimitive.long },
                lex.encode(text, interleaveBlank = true), "idsWithBlank for $text",
            )
        }
    }

    @Test
    fun `lexicon takes the longest match`() {
        // あい, あいさつ and あいかわらず all start the same way. Taking the
        // shortest pronounces a different word.
        val (lex, _) = makeLexicon()
        val full = lex.encode("あいさつ", interleaveBlank = false)
        val short = lex.encode("あい", interleaveBlank = false)
        assertTrue(
            full.size > short.size,
            "あいさつ matched only the あい prefix — this is shortest-match",
        )
    }

    // ── AudioFormat ─────────────────────────────────────────────────────────

    @Test
    fun `audio format matches reference`() {
        val want = readFixture("voice_audio_format.json")["pcm16Mono16k"]!!.jsonObject
        assertEquals(want["sampleRate"]!!.jsonPrimitive.int, AudioFormat.Pcm16Mono16k.sampleRate)
        assertEquals(want["channels"]!!.jsonPrimitive.int, AudioFormat.Pcm16Mono16k.channels)
        assertEquals(
            want["bitsPerSample"]!!.jsonPrimitive.int,
            AudioFormat.Pcm16Mono16k.bitsPerSample,
        )
    }
}
