// PiperVoiceConfig.kt
//
// Kotlin ports of src/CircleAI.Voice/PiperVoiceConfig.cs, LexiconTokeniser.cs
// and AudioFormat.cs.
//
// Parity is asserted against fixtures/voice_piper_config.json,
// fixtures/voice_lexicon_tokeniser.json and fixtures/voice_audio_format.json.

package com.bhengubv.circleai.android.voice

import java.text.Normalizer

/** A PCM audio format expected or produced by voice components. */
data class AudioFormat(val sampleRate: Int, val channels: Int, val bitsPerSample: Int) {
    companion object {
        /**
         * Canonical input format: PCM signed 16-bit, mono, 16 kHz. Most
         * open-source ASR engines (sherpa-onnx, Vosk) accept this directly.
         */
        val PCM16_MONO_16K = AudioFormat(16000, 1, 16)
    }
}

/** What a [PiperVoiceConfig.phonemesToIds] call did, beyond the ids. */
data class PhonemeMapping(
    val ids: List<Long>,
    /** How many symbols the vocabulary had no entry for. */
    val skipped: Int,
    /**
     * WHICH symbols were dropped. A dropped symbol is inaudible, so this list is
     * the only evidence a front-end is broken.
     */
    val skippedSymbols: List<String>,
    /**
     * Symbols APPROXIMATED rather than spoken exactly — a diacritic the voice
     * lacks, folded to its base letter. A compromise, not a success.
     */
    val approximatedSymbols: List<String>,
)

/** A Piper-layout voice's phoneme→id vocabulary and inference settings. */
class PiperVoiceConfig(
    private val map: Map<String, List<Long>>,
    val sampleRate: Int = 22050,
    val noiseScale: Float = 0.667f,
    val lengthScale: Float = 1.0f,
    val noiseW: Float = 0.8f,
    /** e.g. `espeak` (needs a phonemizer) or `text` (graphemes are phonemes). */
    val phonemeType: String = "espeak",
) {

    /** True when this config has a usable phoneme→id map. */
    val hasPhonemeMap: Boolean get() = map.isNotEmpty()

    /**
     * THE PAD RULE: the id THIS voice uses for blank.
     *
     * It is 0 in sherpa/MMS exports and 3 in Piper-family ones, and pointing it
     * at an ordinary vocabulary entry is what made 42 MMS voices speak fluent
     * nonsense. Never assume a constant — read it from the model. Falls back to
     * 0 only when the vocabulary has no `_` at all.
     */
    val padId: Long get() = map[PAD]?.firstOrNull() ?: 0L

    /**
     * Turn a phoneme sequence into model token ids, in piper-phonemize's exact
     * layout with interspersed pad:
     * `[BOS, PAD, id(p1), PAD, id(p2), PAD, …, id(pN), PAD, EOS]`.
     *
     * BOS and EOS appear only when the vocabulary HAS them — the MMS-family
     * exports do not. Unknown symbols are SKIPPED and REPORTED, never fatal.
     */
    fun phonemesToIds(phonemes: List<String>): PhonemeMapping {
        val ids = ArrayList<Long>(64)
        val dropped = ArrayList<String>()
        val approximated = ArrayList<String>()
        var skipped = 0

        map[BOS]?.let { ids.addAll(it) }
        val pad = map[PAD]
        pad?.let { ids.addAll(it) }

        for (phoneme in phonemes) {
            val mapped = mapSymbol(phoneme)
            if (mapped == null) {
                skipped++
                if (!dropped.contains(phoneme)) dropped.add(phoneme)
                continue
            }
            val (seq, wasApprox) = mapped
            if (wasApprox && !approximated.contains(phoneme)) approximated.add(phoneme)
            ids.addAll(seq)
            pad?.let { ids.addAll(it) }
        }

        map[EOS]?.let { ids.addAll(it) }

        return PhonemeMapping(ids, skipped, dropped, approximated)
    }

    private fun mapSymbol(symbol: String): Pair<List<Long>, Boolean>? {
        map[symbol]?.let { return it to false }

        // A grapheme voice's vocabulary is built AFTER the training text has been
        // through the model's own cleaner, and every cleaner in use here
        // lower-cases. Such a vocab contains no capitals at all, so matching on
        // the raw character silently discarded every sentence-initial letter —
        // the model received "awubona" for "Sawubona".
        val lower = symbol.lowercase()
        if (lower != symbol) map[lower]?.let { return it to false }

        // A GRAPHEME CLUSTER the vocabulary stores as separate codepoints.
        // Burmese "ကြို" arrives as ONE symbol while the vocabulary holds each
        // codepoint on its own. Splitting it back keeps every mark, so this must
        // be tried BEFORE any approximation.
        if (symbol.codePointCount(0, symbol.length) > 1) {
            val parts = ArrayList<Long>()
            var whole = true
            var i = 0
            while (i < symbol.length) {
                val cp = symbol.codePointAt(i)
                i += Character.charCount(cp)
                // Zero-width formatting characters shape how text is DRAWN and
                // say nothing about how it sounds. Persian writes them
                // constantly, as do most Indic scripts, and one invisible
                // character was failing the whole cluster.
                if (Character.getType(cp) == Character.FORMAT.toInt()) continue
                val s = String(Character.toChars(cp))
                val part = map[s] ?: map[s.lowercase()]
                if (part == null) { whole = false; break }
                parts.addAll(part)
            }
            if (whole && parts.isNotEmpty()) return parts to false  // exact — nothing lost
        }

        // A letter the voice never learned. Dropping it deletes a consonant from
        // the middle of a word, so an approximation is worth more than a hole —
        // so long as it is declared rather than passed off as correct.
        for (candidate in approximations(symbol)) {
            val a = map[candidate] ?: map[candidate.lowercase()]
            if (a != null) return a to true
        }

        return null
    }

    companion object {
        // Piper's special phoneme symbols (piper-phonemize defaults).
        private const val PAD = "_"
        private const val BOS = "^"
        private const val EOS = "$"

        /**
         * Split into grapheme clusters: a base code point plus any combining
         * marks that follow it, so "bát" is three elements and not four.
         */
        fun splitPhonemeString(s: String): List<String> {
            val out = ArrayList<String>()
            var cur = StringBuilder()
            var i = 0
            while (i < s.length) {
                val cp = s.codePointAt(i)
                val n = Character.charCount(cp)
                val ch = String(Character.toChars(cp))
                if (cur.isNotEmpty() && isCombiningMark(cp)) {
                    cur.append(ch)
                } else {
                    if (cur.isNotEmpty()) out.add(cur.toString())
                    cur = StringBuilder(ch)
                }
                i += n
            }
            if (cur.isNotEmpty()) out.add(cur.toString())
            return out
        }

        private fun approximations(symbol: String): List<String> {
            val out = ArrayList<String>()

            // Where the vocabulary carries the true phoneme under a different
            // spelling, use it — Tshivenda's ṅ IS /ŋ/, so that loses nothing.
            if (symbol == "ṅ" || symbol == "Ṅ") out.add("ŋ")
            if (symbol == "š" || symbol == "Š") out.add("ʃ")

            // Folding a diacritic away is only defensible where the mark modifies
            // a letter that still carries most of the sound without it — Latin
            // š→s, ṱ→t. In Thai, Burmese, Devanagari, Arabic and Vietnamese the
            // marks ARE the vowels and tones; dropping them does not approximate
            // the word, it deletes it. Thai measured 4.3 s instead of ~15 s
            // because every vowel sign was folded off a consonant and filed as a
            // harmless approximation.
            val stripped = stripDiacritics(symbol)
            if (stripped.isEmpty() || stripped == symbol || !isLatinBase(stripped)) return out
            out.add(stripped)
            return out
        }

        /**
         * Judge the BASE that remains, not the composed character: Tshivenda ṱ
         * lives at U+1E71, far above the Latin block, yet strips to a plain 't'.
         * Thai วั strips to ว, which is not Latin at all — the case to refuse.
         */
        private fun isLatinBase(stripped: String): Boolean {
            if (stripped.isEmpty()) return false
            var i = 0
            while (i < stripped.length) {
                val cp = stripped.codePointAt(i)
                if (cp > 0x024F) return false      // beyond Latin Extended-B
                i += Character.charCount(cp)
            }
            return true
        }

        /** Decompose and remove combining marks: ṱ → t. */
        private fun stripDiacritics(s: String): String {
            val d = Normalizer.normalize(s, Normalizer.Form.NFD)
            val sb = StringBuilder(d.length)
            var i = 0
            while (i < d.length) {
                val cp = d.codePointAt(i)
                i += Character.charCount(cp)
                if (!isCombiningMark(cp)) sb.appendCodePoint(cp)
            }
            return sb.toString()
        }

        private fun isCombiningMark(cp: Int): Boolean = when (Character.getType(cp).toByte()) {
            Character.NON_SPACING_MARK,
            Character.COMBINING_SPACING_MARK,
            Character.ENCLOSING_MARK -> true
            else -> false
        }
    }
}

/**
 * Turns text into model tokens using a voice's own lexicon files — a
 * word→phoneme table and a phoneme→id table beside the model. No phonemizer
 * process, no second package, no licence wall.
 */
class LexiconTokeniser private constructor(
    private val words: Map<String, List<Long>>,
    private val longest: Int,
    /** Blank id, interleaved between tokens when the model expects it. */
    var blank: Long,
) {
    /** Symbols the lexicon had no entry for on the last call. */
    var lastUnmapped: List<String> = emptyList()
        private set

    /**
     * Segment [text] and return the model's tokens.
     *
     * LONGEST MATCH FIRST, because these lexicons are word-keyed and the words
     * overlap: あい, あいさつ and あいかわらず all start the same way, and taking
     * the shortest would pronounce a different word. Falls back to the single
     * character when no word matches.
     */
    fun encode(text: String, interleaveBlank: Boolean = true): List<Long> {
        val out = ArrayList<Long>()
        val unmapped = ArrayList<String>()
        // CODE POINTS, NOT UTF-16 UNITS: these lexicons are keyed on CJK words,
        // and an index into the raw string would cut a surrogate pair in half.
        val chars = text.codePoints().toArray().map { String(Character.toChars(it)) }

        var i = 0
        while (i < chars.size) {
            var taken = 0
            val max = minOf(longest, chars.size - i)
            for (len in max downTo 1) {
                val seq = words[chars.subList(i, i + len).joinToString("")]
                if (seq != null) { out.addAll(seq); taken = len; break }
            }
            if (taken == 0) {
                if (chars[i].isNotBlank()) unmapped.add(chars[i])
                taken = 1
            }
            i += taken
        }

        lastUnmapped = unmapped
        if (!interleaveBlank) return out

        // add_blank: a blank opens the utterance and follows every token.
        val padded = ArrayList<Long>(out.size * 2 + 1)
        padded.add(blank)
        for (id in out) { padded.add(id); padded.add(blank) }
        return padded
    }

    companion object {
        /** Build from a voice's `tokens.txt` and `lexicon.txt` content. */
        fun fromText(tokensText: String, lexiconText: String, blank: Long = 0): LexiconTokeniser? {
            // tokens.txt is "<symbol> <id>" per line. The symbol MAY BE A SPACE,
            // so split on the LAST space rather than the first.
            val ids = HashMap<String, Long>()
            for (raw in tokensText.split("\n")) {
                val line = raw.trimEnd('\r')
                val cut = line.lastIndexOf(' ')
                if (cut <= 0) continue
                val id = line.substring(cut + 1).trim().toLongOrNull() ?: continue
                ids[line.substring(0, cut)] = id
            }
            if (ids.isEmpty()) return null

            // lexicon.txt is "<word> <phoneme> <phoneme> ...".
            val words = HashMap<String, List<Long>>()
            var longest = 1
            for (raw in lexiconText.split("\n")) {
                val parts = raw.trimEnd('\r').split(" ").filter { it.isNotEmpty() }
                if (parts.size < 2) continue
                val seq = parts.drop(1).mapNotNull { ids[it] }
                if (seq.isEmpty()) continue
                words[parts[0]] = seq
                val n = parts[0].codePointCount(0, parts[0].length)
                if (n > longest) longest = n
            }
            return if (words.isEmpty()) null else LexiconTokeniser(words, longest, blank)
        }
    }
}
