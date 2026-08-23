// VoiceText.kt
//
// Ports of the five text-side voice modules:
//
//   src/CircleAI.Voice/SentenceSplitter.cs
//   src/CircleAI.Voice/LanguageSpanSplitter.cs
//   src/CircleAI.Voice/GeezRomanizer.cs
//   src/CircleAI.Voice/ToneShaper.cs
//   src/CircleAI.Voice/NchltPhonemizer.cs
//
// Parity is asserted against fixtures/voice_sentence_splitter.json,
// voice_language_spans.json, voice_geez_romanizer.json, voice_tone_shaper.json
// and voice_nchlt_phonemizer.json, which the C# reference generates.

package com.bhengubv.circleai.voice

import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.pow
import kotlin.math.sin
import kotlin.math.sqrt

// ── SentenceSplitter ────────────────────────────────────────────────────────
//
// Why this has to exist: the voices in use here were trained on text with the
// punctuation stripped out, so their vocabularies contain no '.', ',', '?' or
// ':' at all. Feeding a paragraph in one pass produces one unbroken run of
// speech — no pause between sentences, because there is no token that could
// encode one. The pause has to come from outside the model.
//
// It splits at SENTENCE boundaries only, never at commas. Each synthesis is an
// independent utterance and a VITS model ends every utterance with falling,
// sentence-final prosody, so cutting at a comma would make each clause land like
// a finished sentence — worse prosody than the run-on it was meant to fix.

/**
 * One unit of speech, plus the silence that should follow it.
 *
 * @param text The text to synthesise. Never empty or whitespace.
 * @param trailingPauseMs Silence to append after this segment, in milliseconds.
 *   0 for the final segment — trailing silence at the end of a passage serves
 *   nothing.
 */
data class SpeechSegment(val text: String, val trailingPauseMs: Int)

object SentenceSplitter {
    // Pause lengths are the perceptual point of this object, so they are named
    // rather than buried. A full stop reads longer than a colon; a paragraph
    // break longer than either.
    private const val SENTENCE_PAUSE_MS = 280
    private const val CLAUSE_PAUSE_MS = 200 // ':' and ';' — a lighter break
    private const val PARAGRAPH_PAUSE_MS = 400
    private const val FORCED_PAUSE_MS = 60 // an over-long run cut for latency

    /**
     * Beyond this many characters a segment is cut even without punctuation. A
     * single unbroken clause of this size is already several seconds of audio,
     * and on a phone the whole segment must render before ANY of it can play.
     * The cut is taken at a word boundary and given only a token pause.
     */
    const val MAX_CHARS_PER_SEGMENT = 220

    /**
     * Characters that end a sentence, across the scripts we speak.
     *
     * A Latin-only list silently under-splits every language that punctuates
     * differently. Measured on the P30: Hindi, Bengali and Urdu produced THREE
     * segments from the same five-sentence text that gave six in eleven other
     * languages, because Devanagari and Bengali end sentences with the danda and
     * Urdu with its own full stop — none of which were listed. The paragraph ran
     * together exactly as it did before the splitter existed, for about a billion
     * people, and nothing failed loudly enough to notice.
     */
    private val TERMINATORS = setOf(
        '.', '!', '?', ':', ';',      // Latin / Cyrillic / Greek
        '।', '॥',           // danda, double danda — Devanagari, Bengali, Gurmukhi
        '۔', '؟', '؛', // Arabic script — Urdu, Arabic, Persian, Pashto
        '。', '！', '？', // CJK ideographic + fullwidth
        '．', '：', '；', // fullwidth
        '።',                     // Ethiopic — Amharic, Tigrinya
        '។',                     // Khmer khan
        '၊', '။',           // Myanmar little/section
    )

    /**
     * Terminators that can legitimately appear inside a token, and so need a
     * following space before they may be read as ending a sentence.
     */
    private val MAY_OCCUR_INSIDE_A_TOKEN = setOf('.', ':', ';')

    private val CLOSERS = setOf('"', '\'', ')', ']')

    /**
     * Splits [text] into segments. Returns a single segment when there is no
     * sentence punctuation, and an empty list for blank input.
     */
    fun split(text: String?): List<SpeechSegment> {
        val segments = mutableListOf<SpeechSegment>()
        if (text.isNullOrBlank()) return segments

        var current = StringBuilder()
        val pending = SENTENCE_PAUSE_MS

        for (i in text.indices) {
            val c = text[i]

            if (c == '\r') continue
            if (c == '\n') {
                current = flush(segments, current, PARAGRAPH_PAUSE_MS)
                continue
            }

            current.append(c)

            if (c in TERMINATORS && endsSentence(text, i)) {
                current = flush(
                    segments, current,
                    if (c == ':' || c == ';') CLAUSE_PAUSE_MS else SENTENCE_PAUSE_MS,
                )
                continue
            }

            if (current.length >= MAX_CHARS_PER_SEGMENT) {
                current = cutAtWordBoundary(segments, current)
            }
        }

        flush(segments, current, pending)

        // Nothing should follow the last word — a trailing pause is dead air.
        if (segments.isNotEmpty()) {
            segments[segments.size - 1] = segments.last().copy(trailingPauseMs = 0)
        }

        return segments
    }

    /**
     * True when the terminator at [i] really ends a sentence.
     *
     * A period between digits is a decimal ("3.5"), and one followed directly by
     * a letter is usually an abbreviation or a URL — splitting there would cut a
     * word in half and insert a pause inside it.
     */
    private fun endsSentence(text: String, i: Int): Boolean {
        // Absorb any run of closing punctuation ("...", "?!", ".").
        var j = i + 1
        while (j < text.length && (text[j] in TERMINATORS || text[j] in CLOSERS)) j++

        if (j >= text.length) return true // end of input

        // Only SOME terminators can appear inside a token — '.' in 3.5 and co.za,
        // ':' in 12:30. For those, a following space is what separates a sentence
        // end from a decimal point. The rest cannot occur mid-token in any script,
        // and demanding a space after them would never split Chinese, Japanese,
        // Khmer, Thai or Burmese at all: those scripts write without spaces
        // between words, so their full stop is followed by the next letter.
        if (text[i] !in MAY_OCCUR_INSIDE_A_TOKEN) return true

        if (!text[j].isWhitespace()) return false // 3.5, e.g., co.za

        if (text[i] == '.' && i > 0 && text[i - 1].isDigit() &&
            j + 1 < text.length && text[j + 1].isDigit()
        ) {
            return false
        }

        return true
    }

    private fun flush(
        segments: MutableList<SpeechSegment>,
        current: StringBuilder,
        pauseMs: Int,
    ): StringBuilder {
        val s = current.toString().trim()
        if (s.isEmpty()) return StringBuilder()

        // The terminator STAYS in the segment text, deliberately. It is tempting
        // to strip it — this object has already turned it into a pause, and the
        // MMS voices have no token for it. But the SA-11 voice's vocabulary DOES
        // carry '?' and '.', so it can render a real question rise that no
        // inserted silence could imitate. Stripping would have discarded that
        // from all eleven South African languages to tidy up a log line.

        // A segment of nothing but punctuation has no sound to make, and the
        // voice has no token for it either.
        if (s.none { it.isLetterOrDigit() }) return StringBuilder()

        segments.add(SpeechSegment(s, pauseMs))
        return StringBuilder()
    }

    /**
     * Cuts an over-long run at the last space, so the break lands between words
     * rather than inside one. With no space to use the run is left intact — a
     * mid-word cut would be audibly worse than a long segment.
     */
    private fun cutAtWordBoundary(
        segments: MutableList<SpeechSegment>,
        current: StringBuilder,
    ): StringBuilder {
        val s = current.toString()
        val cut = s.lastIndexOf(' ')
        if (cut <= 0) return current

        val head = s.substring(0, cut).trim()
        if (head.isNotEmpty()) segments.add(SpeechSegment(head, FORCED_PAUSE_MS))

        return StringBuilder(s.substring(cut + 1))
    }
}

// ── LanguageSpanSplitter ────────────────────────────────────────────────────
//
// People do not speak one language per sentence. "Igama lami ngu-CircleAI" is
// isiZulu with an English name inside it, and read wholly in isiZulu the name
// comes out mangled — the listener hears the machine fail at a word they know
// perfectly well. A multi-lingual model takes ONE language id per utterance, so
// the fix is to cut the text where the language changes and synthesise each run
// under its own id.

/**
 * A run of text to be spoken in one language.
 *
 * @param text The words, with their spacing preserved.
 * @param isForeign True when this run is the embedded language (English), false
 *   for the surrounding one. The caller maps that to whatever ids its model uses.
 */
data class LanguageSpan(val text: String, val isForeign: Boolean)

object LanguageSpanSplitter {

    /**
     * Splits [text] into spans. Returns a single span when the text is all one
     * language, which is the overwhelmingly common case — callers can check
     * `size == 1` and take their existing single-language path.
     */
    fun split(text: String?): List<LanguageSpan> {
        if (text.isNullOrBlank()) return emptyList()

        val spans = mutableListOf<LanguageSpan>()
        val current = StringBuilder()
        var currentIsForeign: Boolean? = null

        var i = 0
        while (i < text.length) {
            // Separators (spaces, punctuation, the hyphen in "ngu-CircleAI") ride
            // along with whatever run they FOLLOW, so a language change never
            // strands a comma on its own or splits mid-punctuation.
            if (!text[i].isLetterOrDigit()) {
                val sepStart = i
                while (i < text.length && !text[i].isLetterOrDigit()) i++
                current.append(text, sepStart, i)
                continue
            }

            val wordStart = i
            while (i < text.length && text[i].isLetterOrDigit()) i++
            val word = text.substring(wordStart, i)
            val foreign = isForeignWord(word)

            if (currentIsForeign != null && currentIsForeign != foreign) {
                // The run ends at the last word, not at the separators that follow
                // it — those have already been appended and belong to the join.
                spans.add(LanguageSpan(current.toString(), currentIsForeign))
                current.clear()
            }

            currentIsForeign = foreign
            current.append(word)
        }

        if (current.isNotEmpty() && currentIsForeign != null) {
            spans.add(LanguageSpan(current.toString(), currentIsForeign))
        }

        return spans
    }

    /**
     * Rewrites a run into the form a voice can actually pronounce, without
     * changing what is displayed.
     *
     * A compound like `CircleAI` is one token to a synthesiser and it has no idea
     * where the words are, so it produces a mumble. Written `Circle AI` it is two
     * things the voice already knows how to say. This is why the name came out
     * garbled even after it was correctly switched to English — the language was
     * right and the word was still unreadable.
     */
    fun toSpokenForm(text: String): String {
        if (text.isEmpty()) return text

        // 1. Break the compound into words at case boundaries, which is where the
        //    word boundaries genuinely are in this naming style.
        val spaced = StringBuilder(text.length + 4)
        for (i in text.indices) {
            val c = text[i]
            if (i > 0 && c.isUpperCase()) {
                val prev = text[i - 1]
                val next = if (i + 1 < text.length) text[i + 1] else '\u0000'

                // lower->Upper is a word boundary (Circle|AI, You|Tube).
                val afterLower = prev.isLowerCase()
                // Upper->Upper->lower ends a run of capitals (API|Key).
                val endOfAcronym = prev.isUpperCase() && next.isLowerCase()

                if (afterLower || endOfAcronym) spaced.append(' ')
            }
            spaced.append(c)
        }

        // 2. Punctuate the acronyms. "AI" as a bare token gets read as a word —
        //    "ay" — where "A.I." is read as the letters, which is what it is. The
        //    full stops are for the voice, not the reader.
        val s = spaced.toString()
        val out = StringBuilder(s.length + 8)
        var i = 0
        while (i < s.length) {
            if (!s[i].isUpperCase()) {
                out.append(s[i])
                i++
                continue
            }

            val start = i
            while (i < s.length && s[i].isUpperCase()) i++
            val run = s.substring(start, i)

            // A lone capital is an ordinary word opening ("Sawubona"), not an
            // acronym, and a run followed by lowercase was already split above.
            if (run.length < 2) {
                out.append(run)
                continue
            }

            for (ch in run) out.append(ch).append('.')
        }
        return out.toString()
    }

    /**
     * Is this token unmistakably foreign (English) inside African-language text?
     *
     * Two signals only, both chosen because native orthographies do not produce
     * them:
     *
     *   internal capitals     — CircleAI, WhatsApp, MTN's brand spellings
     *   all-caps, 2-5 letters — GPS, SMS, ATM, PIN
     *
     * isiZulu, isiXhosa, Sesotho and the rest capitalise the first letter of a
     * sentence or a proper noun and nothing else, so neither pattern arises
     * naturally. A sentence-initial capital is therefore NOT a signal, which is
     * why only capitals after position zero count.
     *
     * It does NOT try to spot ordinary lowercase English words like "computer" —
     * that needs a lexicon per language pair, and guessing wrong is worse than
     * not guessing: mispronouncing a native word to "fix" a foreign one insults
     * the speaker in their own language.
     */
    fun isForeignWord(word: String): Boolean {
        if (word.length < 2) return false

        var upper = 0
        var lower = 0
        var hasInternalCapital = false

        for (i in word.indices) {
            val c = word[i]
            if (!c.isLetter()) continue
            if (c.isUpperCase()) {
                upper++
                if (i > 0) hasInternalCapital = true
            } else {
                lower++
            }
        }

        if (hasInternalCapital && lower > 0) return true // CircleAI, WhatsApp
        if (upper >= 2 && lower == 0 && word.length <= 5) return true // GPS, SMS, ATM
        return false
    }
}

// ── GeezRomanizer ───────────────────────────────────────────────────────────
//
// Ethiopic (Ge'ez) script -> Latin, because the Amharic and Tigrinya voices do
// not read Ethiopic at all. Meta ships those two MMS models with
// `is_uroman: true`: their vocabularies are 28 and 27 LATIN letters and they
// expect text already transliterated. Measured on the P30, Amharic lost 43
// distinct characters and produced 3.2 s of noise for a 15 s paragraph.
//
// The transliteration is computed, not tabulated, because Unicode lays the
// syllabary out exactly as the script is taught: each consecutive block of EIGHT
// codepoints is one consonant across its vowel orders.

object GeezRomanizer {
    private const val BASE = 0x1200
    private const val ORDERS_PER_CONSONANT = 8

    /**
     * Last codepoint that follows the eight-orders-per-consonant layout. The
     * syllabary ends here; everything above is lone syllables, marks and
     * numerals, and treating any of it as a row invents a pronunciation.
     */
    private const val LAST_SYLLABLE = 0x1357

    /**
     * Consonant per 8-codepoint row, in Unicode order. ASCII only: these voices
     * hold 27-28 plain Latin letters, so a transliteration carrying the Ethiopist
     * diacritics would be dropped as surely as the Ethiopic was.
     *
     * Six rows are LABIALISED — the consonant carries a built-in /w/. Writing
     * them plain turns "kwa" into "ka", which silently changes the word.
     */
    private val CONSONANTS = arrayOf(
        "h", "l", "h", "m", "s", "r", "s", "sh",
        "q", "qw", "q", "qw", "b", "v", "t", "ch",
        "h", "hw", "n", "ny", "", "k", "kw", "k",
        "kw", "w", "", "z", "zh", "y", "d", "d",
        "j", "g", "gw", "ng", "t", "ch", "p", "ts",
        "ts", "f", "p",
    )

    /**
     * Vowel per order. The sixth is SILENT — it marks a bare consonant, which is
     * why the greeting romanises with no trailing vowel.
     */
    private val VOWELS = arrayOf("e", "u", "i", "a", "e", "", "o", "wa")

    /**
     * The three syllables Unicode assigns singly rather than as a row of eight.
     * They are already in the -a order, so the vowel is part of the value.
     */
    private val LONE_SYLLABLES = mapOf(
        'ፘ' to "rya",
        'ፙ' to "mya",
        'ፚ' to "fya",
    )

    /**
     * Combining marks. They modify the syllable before them and have no sound of
     * their own, so they are dropped rather than passed through — a bare mark
     * reaching a Latin-only vocabulary is one more unmapped symbol.
     */
    private val MARKS = setOf('፝', '፞', '፟')

    /** Ethiopic punctuation, mapped so sentence splitting still works. */
    private val PUNCTUATION = mapOf(
        '፠' to " ", // section
        '፡' to " ", // word separator
        '።' to ".", // full stop
        '፣' to ",", // comma
        '፤' to ";", // semicolon
        '፥' to ":", // colon
        '፦' to ":", // preface colon
        '፧' to "?", // question mark
        '፨' to " ", // paragraph separator
    )

    /** True when [text] contains any Ethiopic character. */
    fun isEthiopic(text: String?): Boolean {
        if (text.isNullOrEmpty()) return false
        return text.any { it.code in 0x1200..0x139F }
    }

    /**
     * Ethiopic -> Latin. Characters outside the script pass through untouched, so
     * mixed text (numerals, Latin names, punctuation) survives intact.
     */
    fun romanize(text: String?): String {
        if (text.isNullOrEmpty()) return text ?: ""

        val sb = StringBuilder(text.length * 2)
        for (c in text) {
            val p = PUNCTUATION[c]
            if (p != null) { sb.append(p); continue }

            // THE EIGHT-PER-CONSONANT LAYOUT STOPS AT U+1357, and the range check
            // has to stop with it. Beyond that the block is no longer a syllabary:
            // U+1358..U+135A are three LONE syllables already in their -a order,
            // U+135D..U+135F are combining marks, and U+1369 onward are the
            // numerals. Sizing the check off the consonant table instead swept
            // seven of those numerals back into the syllabary — and they came out
            // as sound, so nothing failed.
            if (c in MARKS) continue
            val lone = LONE_SYLLABLES[c]
            if (lone != null) { sb.append(lone); continue }

            val i = c.code - BASE
            if (i < 0 || i > LAST_SYLLABLE - BASE) {
                // Numerals and the rarely-used supplement blocks have no sound we
                // can render; anything else is not Ethiopic and is left alone.
                if (c.code in 0x1369..0x137C) continue
                sb.append(c)
                continue
            }

            val row = i / ORDERS_PER_CONSONANT
            val order = i % ORDERS_PER_CONSONANT

            val consonant = CONSONANTS[row]
            var vowel = VOWELS[order]

            if (consonant.isEmpty()) {
                // The glottal and pharyngeal rows write no consonant in Latin, so
                // the vowel IS the character. First order is heard as "a", and the
                // sixth — silent after a real consonant — must still sound here, or
                // the word-initial one disappears entirely.
                if (order == 0) vowel = "a"
                else if (vowel.isEmpty()) vowel = "e"
            }

            sb.append(consonant).append(vowel)
        }
        return sb.toString()
    }
}

// ── ToneShaper ──────────────────────────────────────────────────────────────
//
// Warmth, after the model has finished.
//
// THE VOICE WAS REPORTED AS TINNY, AND THE SPEAKER COULD NOT FIX IT. Choosing a
// speaker by how well the recogniser understands it has a bias nobody costed:
// word error rate rewards crisp consonants and a bright top end, which is what
// "tinny" describes. Measured across all 130 speakers in the bundle, warmth and
// intelligibility are inversely related. So the speaker is not the lever. The
// waveform is, and it is entirely ours once the model hands it over.
//
// WHY A DIP AND NOT JUST A BOOST. A phone speaker cannot move enough air to
// reproduce a low-shelf boost; on a P30 the bass simply is not there to lift.
// Cutting 2-5 kHz, where harshness lives, works on hardware that cannot do bass,
// which is most of the hardware this ships to. The boost is for headphones. Both
// are applied because the product is used on both.

/** Biquad coefficients, already normalised by a0. */
data class BiquadCoefficients(val b: DoubleArray, val a: DoubleArray) {
    override fun equals(other: Any?): Boolean =
        other is BiquadCoefficients && b.contentEquals(other.b) && a.contentEquals(other.a)

    override fun hashCode(): Int = 31 * b.contentHashCode() + a.contentHashCode()
}

/**
 * @param lowShelfHz Where the low shelf starts lifting, in Hz.
 * @param lowShelfDb How much to lift the bottom, in dB.
 * @param presenceHz Centre of the harshness dip, in Hz.
 * @param presenceDb How much to cut there, in dB. Negative cuts.
 * @param presenceQ Width of the dip. Lower is wider.
 */
data class ToneShaperSettings(
    val lowShelfHz: Double = 320.0,
    val lowShelfDb: Double = 4.0,
    val presenceHz: Double = 3200.0,
    val presenceDb: Double = -4.0,
    val presenceQ: Double = 0.8,
)

object ToneShaper {
    /** The measured setting: warmer, with no cost to intelligibility. */
    val WARM = ToneShaperSettings()

    private const val LOW_SHELF_SLOPE = 0.9

    private fun normalise(b: DoubleArray, a: DoubleArray): BiquadCoefficients {
        val a0 = a[0]
        for (i in 0 until 3) { b[i] /= a0; a[i] /= a0 }
        return BiquadCoefficients(b, a)
    }

    /** RBJ audio-cookbook low shelf, normalised by a0. */
    fun lowShelf(s: ToneShaperSettings, rate: Int): BiquadCoefficients {
        val amp = 10.0.pow(s.lowShelfDb / 40)
        val w0 = 2 * PI * s.lowShelfHz / rate
        val alpha = sin(w0) / 2 * sqrt((amp + 1 / amp) * (1 / LOW_SHELF_SLOPE - 1) + 2)
        val c = cos(w0)
        val s2 = 2 * sqrt(amp) * alpha

        return normalise(
            doubleArrayOf(
                amp * ((amp + 1) - (amp - 1) * c + s2),
                2 * amp * ((amp - 1) - (amp + 1) * c),
                amp * ((amp + 1) - (amp - 1) * c - s2),
            ),
            doubleArrayOf(
                (amp + 1) + (amp - 1) * c + s2,
                -2 * ((amp - 1) + (amp + 1) * c),
                (amp + 1) + (amp - 1) * c - s2,
            ),
        )
    }

    /** RBJ audio-cookbook peaking EQ, normalised by a0. */
    fun peaking(s: ToneShaperSettings, rate: Int): BiquadCoefficients {
        val amp = 10.0.pow(s.presenceDb / 40)
        val w0 = 2 * PI * s.presenceHz / rate
        val alpha = sin(w0) / (2 * s.presenceQ)
        val c = cos(w0)

        return normalise(
            doubleArrayOf(1 + alpha * amp, -2 * c, 1 - alpha * amp),
            doubleArrayOf(1 + alpha / amp, -2 * c, 1 - alpha / amp),
        )
    }

    /**
     * Direct-form-I biquad, in place.
     *
     * THE STATE IS DOUBLE AND THE STORED SAMPLE IS FLOAT, and both halves matter.
     * The filter memory never sees the float rounding — y1 keeps the
     * full-precision result — so the recursion is identical everywhere. Only what
     * lands in the buffer is narrowed, which is what the next stage then reads.
     */
    fun biquad(x: FloatArray, c: BiquadCoefficients) {
        var x1 = 0.0; var x2 = 0.0; var y1 = 0.0; var y2 = 0.0
        for (i in x.indices) {
            val xn = x[i].toDouble()
            val yn = c.b[0] * xn + c.b[1] * x1 + c.b[2] * x2 - c.a[1] * y1 - c.a[2] * y2
            x2 = x1; x1 = xn
            y2 = y1; y1 = yn
            x[i] = yn.toFloat()
        }
    }

    private fun peak(x: FloatArray): Float {
        var p = 0f
        for (v in x) { val a = abs(v); if (a > p) p = a }
        return p
    }

    /**
     * Filters [waveform] in place with a low shelf and a presence dip in series.
     *
     * PEAK IS RESTORED AFTERWARDS. Lifting the low shelf adds energy, and a
     * waveform that already peaked near full scale would clip — which is heard as
     * crackle and would be blamed on the quantised model rather than on this.
     * Scaling back to the original peak keeps the tone change audible and the
     * level unchanged.
     */
    fun apply(waveform: FloatArray, sampleRate: Int, settings: ToneShaperSettings = WARM) {
        if (waveform.isEmpty() || sampleRate <= 0) return

        val before = peak(waveform)
        if (before <= 0f) return // a silent buffer, and dividing by that peak is NaN

        biquad(waveform, lowShelf(settings, sampleRate))
        biquad(waveform, peaking(settings, sampleRate))

        val after = peak(waveform)
        if (after > 0f && after > before) {
            // Float division, because the reference divides two FLOATS here.
            // Widening to double makes the gain a few ULP different and the whole
            // tail of the waveform drifts with it.
            val g = before / after
            for (i in waveform.indices) waveform[i] *= g
        }
    }
}

// ── NchltPhonemizer ─────────────────────────────────────────────────────────
//
// A fully sovereign, permissive-licence grapheme-to-phoneme front-end for the
// South African languages. NOT espeak-ng (GPLv3 taints the app), NOT phonemeza
// (unlicensed, weights unpublished), and not neural. A faithful port of the
// NCHLT pronunciation predictor (Marelie Davel, pron_predict.pl) driven by the
// NCHLT-inlang resources, © DAC / CSIR / NWU under CC BY 3.0.
//
// Because the rule set covers any word there is no "OOV gap": a word is either in
// the dictionary (exact) or synthesised by the rules, which is what makes
// agglutinative isiZulu tractable.

class NchltPhonemizer private constructor(
    private val dict: Map<String, List<String>>,
    private val rules: Map<Char, List<Rule>>,
    private val phoneMap: Map<Char, String>,
    private val graphMap: Map<Char, Char>,
    private val gnulls: List<Pair<String, String>>,
) {
    /** One context rule: grapheme `g` in left/right context -> code. */
    private data class Rule(val order: Int, val left: String, val right: String, val code: String)

    /**
     * Words in the last [phonemize] call that were synthesised by the rule engine
     * rather than found in the dictionary. A coverage diagnostic, never a failure
     * — the rules always produce output.
     */
    var lastRulePredictedWords: Int = 0
        private set

    /**
     * Graphemes in the last call that no rule covered. Skipped, never guessed.
     */
    private val unknown = mutableListOf<String>()
    val lastUnknownGraphemes: List<String> get() = unknown

    fun phonemize(text: String): List<String> {
        lastRulePredictedWords = 0
        unknown.clear()
        if (text.isBlank()) return emptyList()

        val phones = mutableListOf<String>()
        for (word in tokenize(text)) {
            val known = dict[word]
            if (known != null) {
                phones.addAll(known)
            } else {
                phones.addAll(predictWord(word))
                lastRulePredictedWords++
            }
        }
        return phones
    }

    /**
     * Predict a single word's X-SAMPA phones from the context rules — the exact
     * algorithm of `g2p_word_olist`: for each grapheme take the highest-order rule
     * whose left/right context matches, emit its code, drop nulls, then remap
     * codes to X-SAMPA.
     */
    fun predictWord(word: String): List<String> {
        // Does NOT clear the unknown list, matching the reference: phonemize()
        // owns the reset, so a direct call accumulates rather than hiding what an
        // earlier word already reported.
        if (word.isEmpty()) return emptyList()

        // Grapheme remap (usually identity) then grapheme-null insertion.
        val w = applyGnulls(mapGraphemes(word))

        val codes = mutableListOf<Char>()
        for (i in w.indices) {
            val g = w[i]
            val gRules = rules[g]
            if (gRules == null) {
                // Skip an unknown grapheme rather than fabricate a phone for it.
                val s = g.toString()
                if (s !in unknown) unknown.add(s)
                continue
            }

            // pat = " " + left-context + "-" + g + "-" + right-context + " "
            val pat = " " + w.substring(0, i) + "-" + g + "-" + w.substring(i + 1) + " "

            // Rules are pre-sorted most-specific-first; the first match wins.
            var code = '0'
            for (r in gRules) {
                if (pat.contains(r.left + "-" + g + "-" + r.right)) {
                    code = if (r.code.isNotEmpty()) r.code[0] else '0'
                    break
                }
            }
            if (code != '0') codes.add(code)
        }

        return codes.map { phoneMap[it] ?: it.toString() }
    }

    private fun mapGraphemes(word: String): String {
        if (graphMap.isEmpty()) return word
        val sb = StringBuilder(word.length)
        for (c in word) sb.append(graphMap[c] ?: c)
        return sb.toString()
    }

    private fun applyGnulls(word: String): String {
        var w = word
        for ((from, to) in gnulls) w = w.replace(from, to)
        return w
    }

    companion object {
        /**
         * Build from the file CONTENTS rather than paths, so a caller can load
         * from an embedded resource or a downloaded bundle with no filesystem in
         * reach.
         */
        fun fromText(
            dictText: String,
            rulesText: String,
            phoneMapText: String,
            graphMapText: String? = null,
            gnullsText: String? = null,
        ): NchltPhonemizer = NchltPhonemizer(
            parseDict(dictText),
            parseRules(rulesText),
            parsePhoneMap(phoneMapText),
            if (graphMapText.isNullOrEmpty()) emptyMap() else parseGraphMap(graphMapText),
            if (gnullsText.isNullOrEmpty()) emptyList() else parseGnulls(gnullsText),
        )

        /**
         * Lower-case and split into word tokens on anything that is not a letter.
         * Diacritics are preserved (Afrikaans ê/ë/ô are real graphemes); digits
         * and punctuation become separators. Number and abbreviation expansion is
         * out of scope and belongs to a text-normalisation pass upstream.
         */
        private fun tokenize(text: String): List<String> {
            val words = mutableListOf<String>()
            val sb = StringBuilder()
            for (ch in text.trim()) {
                if (ch.isLetter()) {
                    sb.append(ch.lowercaseChar())
                } else if (sb.isNotEmpty()) {
                    words.add(sb.toString())
                    sb.clear()
                }
            }
            if (sb.isNotEmpty()) words.add(sb.toString())
            return words
        }

        /** Split the way a StreamReader does, so a CRLF file parses identically. */
        private fun lines(text: String): List<String> =
            text.split("\n").map { it.removeSuffix("\r") }

        private fun parseDict(text: String): Map<String, List<String>> {
            val dict = LinkedHashMap<String, List<String>>()
            for (line in lines(text)) {
                if (line.isEmpty()) continue
                val tab = line.indexOf('\t')
                if (tab <= 0) continue
                val word = line.substring(0, tab)
                val pron = line.substring(tab + 1).trim()
                if (pron.isEmpty() || dict.containsKey(word)) continue // keep the FIRST variant
                dict[word] = pron.split(' ').filter { it.isNotEmpty() }
            }
            return dict
        }

        private fun parseRules(text: String): Map<Char, List<Rule>> {
            val byGrapheme = LinkedHashMap<Char, MutableList<Rule>>()
            for (line in lines(text)) {
                if (line.isEmpty()) continue
                // grapheme ; left ; right ; code ; order [ ; count ]
                val f = line.split(';')
                if (f.size < 5 || f[0].isEmpty()) continue
                val order = f[4].trim().toIntOrNull() ?: continue
                byGrapheme.getOrPut(f[0][0]) { mutableListOf() }
                    .add(Rule(order, f[1], f[2], f[3]))
            }

            // STABLE sort, descending by order. Two rules of equal order must stay
            // in file order — the reference uses LINQ's OrderByDescending, which is
            // stable, and a port that reaches for an unstable sort will disagree on
            // ties in exactly the dense rule sets where ties are common. Kotlin's
            // sortedByDescending is stable.
            return byGrapheme.mapValues { (_, v) -> v.sortedByDescending { it.order } }
        }

        private fun parsePhoneMap(text: String): Map<Char, String> {
            // Line: "<code>\t<xsampa>"  (code is a single char).
            val map = LinkedHashMap<Char, String>()
            for (line in lines(text)) {
                if (line.isEmpty()) continue
                val tab = line.indexOf('\t')
                if (tab <= 0) continue
                val code = line.substring(0, tab)
                if (code.length == 1) map[code[0]] = line.substring(tab + 1)
            }
            return map
        }

        private fun parseGraphMap(text: String): Map<Char, Char> {
            // File line: "<funny>\t<std>" — we map std->funny (per remap_dict's gmap).
            val map = LinkedHashMap<Char, Char>()
            for (line in lines(text)) {
                if (line.isEmpty()) continue
                val f = line.split('\t')
                if (f.size == 2 && f[0].length == 1 && f[1].length == 1 && f[0][0] != f[1][0]) {
                    map[f[1][0]] = f[0][0]
                }
            }
            return map
        }

        private fun parseGnulls(text: String): List<Pair<String, String>> {
            // File line: "<from>;<to>" — insert grapheme-nulls (empty for Nguni).
            val list = mutableListOf<Pair<String, String>>()
            for (line in lines(text)) {
                if (line.isEmpty()) continue
                val f = line.split(';')
                if (f.size == 2) list.add(f[0] to f[1])
            }
            return list
        }
    }
}
