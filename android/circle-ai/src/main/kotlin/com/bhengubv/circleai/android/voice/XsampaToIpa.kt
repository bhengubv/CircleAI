// XsampaToIpa.kt
//
// Kotlin port of src/CircleAI.Voice/XsampaToIpa.cs and SentencePieceUnigram.cs.
//
// Parity is asserted against fixtures/voice_xsampa_to_ipa.json and
// fixtures/voice_sentencepiece_unigram.json, which the C# reference generates.
// If this file and those disagree, one of them is wrong and the test names the
// case.

package com.bhengubv.circleai.android.voice

import java.text.Normalizer

/** X-SAMPA → IPA for the 11 South African languages. */
object XsampaToIpa {

    /**
     * Every phone in the NCHLT Afrikaans dictionary, mapped to IPA.
     *
     * Derived from the corpus, not from memory: exactly the distinct phones in
     * nchlt_afr.dict, with every IPA character checked against the target
     * voice's own token table before the table was written.
     */
    private val map: Map<String, String> = mapOf(
        // Vowels
        "a" to "a", "A:" to "ɑː", "A:r" to "ɑːr",
        "E" to "ɛ", "O" to "ɔ", "@" to "ə",
        "i" to "i", "u" to "u", "y" to "y",
        "9" to "œ", "2:" to "øː", "{" to "æ",

        // Diphthongs — NCHLT gives one token, the voice wants both elements.
        "9y" to "œy", "@i" to "əi", "@u" to "əu",
        "i@" to "iə", "u@" to "uə",

        // Consonants
        "b" to "b", "d" to "d", "f" to "f",
        // U+0261 LATIN SMALL LETTER SCRIPT G — the IPA letter, NOT ASCII 'g'.
        // The voice's vocabulary carries ɡ; a plain 'g' would miss and be dropped.
        "g" to "ɡ",
        "j" to "j", "k" to "k", "l" to "l",
        "m" to "m", "n" to "n", "N" to "ŋ",
        "p" to "p", "r" to "r", "s" to "s",
        "S" to "ʃ", "t" to "t", "v" to "v",
        "w" to "w", "x" to "x", "z" to "z",
        "Z" to "ʒ",

        // APPROXIMATION, DELIBERATE AND THE ONLY ONE. X-SAMPA h\ is ɦ, the
        // voiced glottal fricative Afrikaans uses in "hond". This voice's
        // vocabulary has no ɦ, only h. Voicing is lost; place and manner are
        // right, so the word stays recognisable.
        "h\\" to "h",
    )

    /** The IPA symbols, and the phones that had no mapping. */
    data class Conversion(val ipa: List<String>, val unmapped: List<String>)

    /**
     * Convert X-SAMPA phone tokens to a flat IPA symbol list.
     *
     * The misses come back with the result rather than being stashed away,
     * because an unmapped phone produces NO SOUND and the audio is merely
     * shorter — every acoustic measure still passes. A caller that cannot see
     * the misses cannot refuse.
     *
     * LONGEST MATCH ON WHOLE TOKENS. Several entries are multi-character (A:r,
     * @i, 9y) and NCHLT emits them as single tokens; matching on the token —
     * never character by character — is what keeps A:r from becoming A + : + r.
     */
    fun convert(xsampa: List<String>): Conversion {
        val ipa = ArrayList<String>(xsampa.size + 8)
        val unmapped = ArrayList<String>()

        for (phone in xsampa) {
            if (phone.isBlank()) continue

            val mapped = map[phone]
            if (mapped != null) {
                // Per CODE POINT, not per Char: Kotlin Strings are UTF-16 and
                // splitting a surrogate pair would produce lone halves. None of
                // the current values are astral, but the loop must not be the
                // thing that assumes so.
                var i = 0
                while (i < mapped.length) {
                    val cp = mapped.codePointAt(i)
                    ipa.add(String(Character.toChars(cp)))
                    i += Character.charCount(cp)
                }
                continue
            }

            if (!unmapped.contains(phone)) unmapped.add(phone)
        }

        return Conversion(ipa, unmapped)
    }

    /** True when every phone in [xsampa] has a mapping. */
    fun canSayAll(xsampa: List<String>): Boolean =
        xsampa.filter { it.isNotBlank() }.all { map.containsKey(it) }

    /** The X-SAMPA phones this table knows — for tests and diagnostics. */
    fun knownPhones(): List<String> = map.keys.toList()
}

/**
 * SentencePiece unigram tokeniser — Viterbi over the piece lattice, with byte
 * fallback.
 */
class SentencePieceUnigram(
    private val ids: Map<String, Int>,
    private val scores: Map<String, Float>,
) {
    private val maxPieceLength: Int =
        ids.keys.maxOfOrNull { it.codePointCount(0, it.length) } ?: 1

    val count: Int get() = ids.size

    /**
     * Encode text to token ids.
     *
     * VITERBI, NOT GREEDY LONGEST-MATCH. Unigram scores are not monotone in
     * piece length — a long piece can score worse than the two short pieces
     * covering the same span — so greedy silently produces plausible-but-wrong
     * segmentations.
     */
    fun encode(text: String): List<Int> {
        if (text.isEmpty()) return emptyList()

        // SentencePiece's own normalisation: NFKC, then spaces become U+2581,
        // with one prepended so the first word is marked word-initial too.
        val normalised = "▁" + Normalizer.normalize(text, Normalizer.Form.NFKC).replace(' ', '▁')

        // CODE POINTS, NOT UTF-16 UNITS. A piece boundary landing inside a
        // surrogate pair produces pieces that match nothing and byte fallback
        // that decodes to a different character.
        val chars: List<String> = normalised.codePoints().toArray()
            .map { String(Character.toChars(it)) }
        val n = chars.size

        val unreachable = -1e18f
        val best = FloatArray(n + 1) { unreachable }
        val fromIndex = IntArray(n + 1)
        val piece = arrayOfNulls<String>(n + 1)
        val hasPiece = BooleanArray(n + 1)
        best[0] = 0f

        for (i in 0 until n) {
            if (best[i] <= unreachable / 2) continue

            val limit = minOf(maxPieceLength, n - i)
            for (len in 1..limit) {
                val candidate = chars.subList(i, i + len).joinToString("")
                if (!ids.containsKey(candidate)) continue
                val score = best[i] + (scores[candidate] ?: 0f)
                if (score > best[i + len]) {
                    best[i + len] = score
                    fromIndex[i + len] = i
                    piece[i + len] = candidate
                    hasPiece[i + len] = true
                }
            }

            // Byte fallback for this ONE code point, so no input is ever silent.
            val end = i + 1
            val fallback = best[i] - FALLBACK_PENALTY
            if (fallback > best[end]) {
                best[end] = fallback
                fromIndex[end] = i
                hasPiece[end] = false
            }
        }

        val reversed = ArrayList<Int>(n)
        var i = n
        while (i > 0) {
            val start = fromIndex[i]
            val p = piece[i]
            if (hasPiece[i] && p != null) {
                ids[p]?.let { reversed.add(it) }
            } else {
                // BACKWARDS, because this whole list is built backwards. The
                // lattice is walked from the end and reversed once at the
                // bottom, so a multi-byte character added in forward order
                // comes out byte-reversed: é is UTF-8 C3 A9 and would be
                // emitted A9 C3. Nothing throws — those are real pieces with
                // real ids — so the model simply says a different character.
                val raw = chars.subList(start, i).joinToString("").toByteArray(Charsets.UTF_8)
                for (b in raw.indices.reversed()) {
                    val key = "<0x%02X>".format(raw[b].toInt() and 0xFF)
                    ids[key]?.let { reversed.add(it) }
                }
            }
            i = start
        }

        return reversed.reversed()
    }

    companion object {
        /**
         * Cost charged for falling back to raw bytes.
         *
         * Any finite penalty works, because fallback only ever competes with
         * "no path at all". It must be worse than a real piece so the lattice
         * never prefers it where a piece exists, and finite so a path always
         * exists.
         */
        private const val FALLBACK_PENALTY = 10.0f
    }
}
