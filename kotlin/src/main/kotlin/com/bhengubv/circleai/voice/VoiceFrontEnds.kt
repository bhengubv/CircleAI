// VoiceFrontEnds.kt
//
// The phonemizers that are not a native library, the Japanese prosody
// tokeniser, the two TTS decorators, the single-graph wake detector's config,
// and the hands-free loop.
//
// Ported from src/CircleAI.Voice/{GeezRomanizer, IPhonemizer, ITtsEngine,
// LexiconPhonemizer, OpenJTalkProsodyTokeniser, PhrasedTtsEngine, Respeller,
// KwsWakeWordDetector, OnnxSpeakerIdentity, VoiceLoop}.cs.

package com.bhengubv.circleai.voice

import java.io.File
import java.util.Locale
import java.util.concurrent.atomic.AtomicBoolean

// ─────────────────────────────────────────────────────────────────────────────
// What a front end can report about what it lost

/**
 * Optional on a TTS engine: what the last synthesis could NOT say.
 *
 * A front end that drops a symbol still produces audio, so a caller has no way
 * to tell a clean render from one that quietly deleted every 'š' in the
 * sentence. Approximations are reported SEPARATELY from outright drops, because
 * an approximation is a declared substitution and a drop is a hole.
 */
interface ITtsFrontEndDiagnostics {
    val lastSkippedCount: Int
    val lastSkippedSymbols: List<String>
    val lastApproximatedSymbols: List<String> get() = emptyList()
}

// ─────────────────────────────────────────────────────────────────────────────
// Ge'ez

/**
 * Ethiopic text for the Amharic and Tigrinya voices, which cannot read it.
 *
 * Meta ships those two MMS models with `is_uroman: true` — their vocabularies
 * are 28 and 27 LATIN letters. Measured on the P30, Amharic fed Ethiopic lost 43
 * distinct characters and produced 3.2 s of noise for a 15 s paragraph. The
 * model has simply never seen an Ethiopic codepoint.
 */
class GeezPhonemizer : IPhonemizer {

    /** What the last call transliterated to. Kept because when a voice sounds
     *  wrong the first question is whether the transliteration or the model is
     *  at fault, and without this there is no way to tell. */
    @Volatile
    var lastRomanised: String = ""
        private set

    override fun phonemize(text: String): List<String> {
        val r = GeezRomanizer.romanize(text)
        lastRomanised = r
        return if (r.isEmpty()) emptyList() else PiperVoiceConfig.splitPhonemeString(r)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tones

/**
 * A phonemizer that also produces a tone per phoneme.
 *
 * Separate from [IPhonemizer] because most languages have no tone channel at
 * all, and a voice that has one needs the two arrays to stay exactly in step.
 */
interface IToneSource {
    val lastTones: List<Long>
}

/**
 * Text to phonemes by DICTIONARY LOOKUP, for scripts that do not encode sound.
 *
 * Chinese characters carry meaning, not sound, so no character-driven model can
 * read them and no letter-to-sound rule helps. The usual answer is a Python G2P
 * library, which cannot run on the phone. But the sherpa-onnx builds ship the
 * mapping as a plain lexicon.txt beside the model — 195,828 entries for
 * Mandarin. A lookup table is something a Kirin 710 can do.
 */
class LexiconPhonemizer private constructor(
    private val lexicon: Map<String, Entry>,
    /** Longest key in CHARACTERS, so the greedy match knows where to start. */
    private val longestEntry: Int
) : IPhonemizer, IToneSource {

    data class Entry(val phones: List<String>, val tones: List<Long>)

    @Volatile
    private var tones: List<Long> = emptyList()

    @Volatile
    private var unknown: List<String> = emptyList()

    override val lastTones: List<Long> get() = tones

    /** Characters the lexicon had no entry for. A voice that reads 90% of a
     *  sentence sounds broken rather than absent, so this is how a caller
     *  learns the dictionary is the problem and not the model. */
    val lastUnknownWords: List<String> get() = unknown

    val entryCount: Int get() = lexicon.size

    override fun phonemize(text: String): List<String> {
        val phones = ArrayList<String>()
        val toneOut = ArrayList<Long>()
        val unknownOut = LinkedHashSet<String>()

        if (text.isEmpty()) {
            tones = toneOut; unknown = unknownOut.toList()
            return phones
        }

        var i = 0
        while (i < text.length) {
            if (text[i].isWhitespace()) { i++; continue }

            var matched = false
            var len = minOf(longestEntry, text.length - i)
            while (len >= 1) {
                val candidate = text.substring(i, i + len)
                val entry = lexicon[candidate] ?: lexicon[candidate.lowercase(Locale.ROOT)]
                if (entry != null) {
                    phones.addAll(entry.phones)
                    // One tone per phone, PADDED with 0. Without the pad the two
                    // arrays drift apart at the first gap and every syllable
                    // after it gets the wrong tone — audible, never an error.
                    for (k in entry.phones.indices) {
                        toneOut.add(entry.tones.getOrElse(k) { 0L })
                    }
                    i += len
                    matched = true
                    break
                }
                len--
            }

            if (!matched) {
                unknownOut.add(text.substring(i, i + 1))
                i++
            }
        }

        tones = toneOut
        unknown = unknownOut.toList()
        return phones
    }

    companion object {
        fun load(path: String): LexiconPhonemizer = parse(File(path).readText())

        fun parse(text: String): LexiconPhonemizer {
            val map = HashMap<String, Entry>()
            var longest = 1

            for (raw in text.lineSequence()) {
                val line = raw.trim()
                if (line.isEmpty()) continue

                val parts = line.split(Regex("\\s+")).filter { it.isNotEmpty() }
                // A word with no pronunciation is unusable.
                if (parts.size < 2) continue

                val word = parts[0]
                val rest = parts.drop(1)

                // A TRAILING RUN OF BARE INTEGERS, EXACTLY HALF THE REMAINDER,
                // is the tone channel. Guessing wrong is silent either way: read
                // as phonemes the digits are looked up and dropped; read as
                // tones half the pronunciation disappears.
                var phoneCount = rest.size
                var toneValues: List<Long> = emptyList()
                if (rest.size % 2 == 0 && rest.isNotEmpty()) {
                    val half = rest.size / 2
                    val tail = rest.drop(half).map { it.toLongOrNull() }
                    if (tail.all { it != null }) {
                        phoneCount = half
                        toneValues = tail.filterNotNull()
                    }
                }

                map[word] = Entry(rest.take(phoneCount), toneValues)
                if (word.length > longest) longest = word.length
            }
            return LexiconPhonemizer(map, longest)
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// espeak-ng, out of process

/**
 * Text to IPA by running espeak-ng.
 *
 * Out of process on purpose: espeak-ng is GPL, and linking it would make this
 * GPL too. A pipe is a boundary the licence respects.
 */
class EspeakPhonemizer(
    private val voice: String = "en-us",
    private val executable: String = "espeak-ng"
) : IPhonemizer {

    override fun phonemize(text: String): List<String> {
        if (text.isBlank()) return emptyList()
        val raw = runCatching { run(text) }.getOrNull() ?: return emptyList()
        return clean(raw)
    }

    private fun run(text: String): String {
        // -q suppresses audio; --ipa=3 prints IPA with no separators, which is
        // exactly the symbol set in Piper's phoneme map.
        val process = ProcessBuilder(executable, "-q", "-v", voice, "--ipa=3")
            .redirectErrorStream(false)
            .start()

        // THE TEXT GOES IN ON STDIN, NOT AS AN ARGUMENT.
        //
        // espeak-ng reads argv through the ANSI code page on Windows, so
        // Devanagari, Cyrillic, Hangul, Bengali, Sinhala and Arabic never reach
        // it — and it exits 0 with EMPTY output rather than failing, which is
        // the silent kind. Passed as an argument, six scripts produced nothing
        // at all; fed on stdin as UTF-8 all six phonemise correctly. Latin
        // survives either way, which is precisely why this hid.
        //
        // AND IT ENDS WITH A NEWLINE, which is not cosmetic. espeak treats a
        // newline as the end of a clause and will not flush the final one
        // without it. Unterminated, the last character is dropped or — worse —
        // read as a Unicode character NAME and spoken in English.
        process.outputStream.bufferedWriter(Charsets.UTF_8).use {
            it.write(text)
            it.write("\n")
        }

        val out = process.inputStream.bufferedReader(Charsets.UTF_8).readText()
        process.errorStream.readBytes()
        process.waitFor()
        return out
    }

    companion object {
        /**
         * Strips espeak's language-switch markers and folds the output to one
         * line. "(en)hello(ko)" — left in, the LETTERS inside the brackets get
         * mapped and spoken aloud.
         */
        internal fun clean(raw: String): List<String> {
            val flat = raw.replace("\r", "").replace("\n", " ").trim()
            val sb = StringBuilder(flat.length)
            var depth = 0
            for (c in flat) {
                when {
                    c == '(' -> depth++
                    c == ')' -> if (depth > 0) depth--
                    depth == 0 -> sb.append(c)
                }
            }
            val cleaned = sb.toString().trim()
            return if (cleaned.isEmpty()) emptyList()
            else PiperVoiceConfig.splitPhonemeString(cleaned)
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Japanese prosody

/**
 * Open JTalk full-context labels to the token ids the Japanese VITS was trained
 * on.
 *
 * JAPANESE IS A FOURTH FAMILY. The other three ONNX voice layouts take phonemes
 * or characters; this one takes PROSODY — accent structure written into the
 * symbol stream as brackets. Feed it bare phonemes and it speaks, flatly and
 * wrongly, with no error anywhere.
 */
class OpenJTalkProsodyTokeniser {

    @Volatile
    var lastSymbols: List<String> = emptyList()
        private set

    /** Symbols the vocabulary did not contain. Each is a silent flat spot in the
     *  prosody, so a caller that cares about quality wants to see this. */
    @Volatile
    var lastUnknown: List<String> = emptyList()
        private set

    fun encode(labels: String): IntArray =
        encode(labels.split('\n').map(String::trim).filter(String::isNotEmpty))

    fun encode(labels: List<String>): IntArray {
        val symbols = ArrayList<String>(labels.size + 8)
        val unknown = ArrayList<String>()

        for (n in labels.indices) {
            val current = labels[n]
            var p3 = currentPhoneme(current) ?: continue

            // DEVOICED VOWELS ARE WRITTEN AS CAPITALS by Open JTalk and are NOT
            // in this vocabulary — the model was trained with them folded into
            // the plain vowels. Without the fold every devoiced vowel becomes
            // <unk>, and that is most sentence-final -masu and -desu.
            if (p3.length == 1 && p3[0] in "AEIOU") p3 = p3.lowercase(Locale.ROOT)

            if (p3 == "sil") {
                // Utterance-boundary silence carries the sentence TYPE rather
                // than a sound: '$' for a statement, '?' for a question — the
                // difference between a flat and a rising final contour.
                when {
                    n == 0 -> symbols.add("^")
                    n == labels.size - 1 -> symbols.add(if (numeric(RE_E3, current) == 1) "?" else "$")
                }
                continue
            }
            if (p3 == "pau") { symbols.add("_"); continue }

            symbols.add(p3)

            // Accent structure, from THIS label and the next mora's position.
            val a1 = numeric(RE_A1, current)
            val a2 = numeric(RE_A2, current)
            val a3 = numeric(RE_A3, current)
            val f1 = numeric(RE_F1, current)
            val a2Next = if (n + 1 < labels.size) numeric(RE_A2, labels[n + 1]) else ABSENT

            // Only a vowel, moraic n, or the geminate can carry a boundary or a
            // pitch movement — a consonant is mid-mora and gets nothing.
            val carries = (p3.length == 1 && p3[0] in "aeiouAEIOUN") || p3 == "cl"

            when {
                a3 == 1 && a2Next == 1 && carries -> symbols.add("#")   // phrase border
                a1 == 0 && a2Next == a2 + 1 && a2 != f1 -> symbols.add("]") // pitch fall
                a2 == 1 && a2Next == 2 -> symbols.add("[")              // pitch rise
            }
        }

        val ids = IntArray(symbols.size)
        for (i in symbols.indices) {
            val id = IDS[symbols[i]]
            if (id != null) ids[i] = id else { ids[i] = UNK_ID; unknown.add(symbols[i]) }
        }

        lastSymbols = symbols
        lastUnknown = unknown
        return ids
    }

    companion object {
        /** The model's own symbol table, in its own order. The ids ARE the
         *  indices, so this list cannot be reordered or tidied. */
        val VOCABULARY = listOf(
            "<blank>", "<unk>", "a", "o", "i", "[", "#", "u", "]", "e", "k", "n",
            "t", "r", "s", "N", "m", "_", "sh", "d", "g", "^", "$", "w", "cl", "h",
            "y", "b", "j", "ts", "ch", "z", "p", "f", "ky", "ry", "gy", "hy", "ny",
            "by", "my", "py", "v", "dy", "?", "ty", "<sos/eos>"
        )

        const val BLANK_ID = 0
        const val UNK_ID = 1

        /** Not present at all. Deliberately far from any real value so an absent
         *  field can never compare equal to a legitimate one — 0 and -1 are both
         *  real answers here. */
        private const val ABSENT = -50

        private val IDS: Map<String, Int> = VOCABULARY.withIndex().associate { it.value to it.index }

        private val RE_A1 = Regex("/A:([0-9\\-]+)\\+")
        private val RE_A2 = Regex("\\+(\\d+)\\+")
        private val RE_A3 = Regex("\\+(\\d+)/")
        private val RE_F1 = Regex("/F:(\\d+)_")
        private val RE_E3 = Regex("!(\\d+)_")
        private val RE_PHONEME = Regex("\\-(.*?)\\+")

        fun symbolFor(id: Int): String = VOCABULARY.getOrElse(id) { "<oob>" }

        internal fun currentPhoneme(label: String): String? =
            RE_PHONEME.find(label)?.groupValues?.get(1)

        internal fun numeric(re: Regex, label: String): Int =
            re.find(label)?.groupValues?.get(1)?.toIntOrNull() ?: ABSENT
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The single-graph wake detector

/** How the classifier's graph wants its input. */
enum class KwsInputKind { Waveform, LogMelFilterbank }

/** How a speaker-embedding graph wants its input. */
enum class SpeakerEmbedderInputKind { Waveform, LogMelFilterbank }

data class KwsConfig(
    val modelPath: String,
    val threshold: Float = 0.7f,
    val inputKind: KwsInputKind = KwsInputKind.LogMelFilterbank,
    val sampleRateHz: Int = 16_000
)

/**
 * The single-phrase classifier, wearing the interface the rest of the system
 * talks to.
 *
 * The GRAPH needs onnxruntime and is supplied as a closure, so the detector's
 * behaviour — debounce, listening state, confidence clamp — is testable without
 * it. It reports `supportsPerPhraseMatching = false` because it genuinely
 * cannot: it scores the ONE phrase it was trained on, and a household wanting a
 * phrase per person needs the transducer.
 */
class KwsWakeWordDetector(
    private val config: KwsConfig,
    private val capture: IAudioCapture,
    /** Returns the score for the current window, or null when it cannot run. */
    private val score: (FloatArray) -> Float?,
    private val nowMs: () -> Long = { System.currentTimeMillis() },
    private val minIntervalBetweenFiresMs: Long = 1200
) : AutoCloseable {

    private val listening = AtomicBoolean(false)
    private var lastFireMs = 0L

    val wakeWord: String get() = File(config.modelPath).nameWithoutExtension
    val supportsPerPhraseMatching: Boolean get() = false
    val isListening: Boolean get() = listening.get()

    var onWakeWordDetected: ((WakeWordDetectedEvent) -> Unit)? = null

    /** Feeds one window and returns whether it fired. */
    @Synchronized
    fun offer(window: FloatArray): Boolean {
        val s = score(window) ?: return false
        if (s < config.threshold) return false

        // The classifier emits a score per window while the phrase is still
        // under the microphone, so one spoken phrase is several detections.
        val now = nowMs()
        if (now - lastFireMs < minIntervalBetweenFiresMs) return false
        lastFireMs = now
        return true
    }

    fun start() { listening.set(true) }
    fun stop() { listening.set(false) }

    override fun close() {
        stop()
        runCatching { capture.close() }
    }
}
