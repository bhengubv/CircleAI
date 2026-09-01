// Respelling.kt
//
// Making an English word sayable by an isiZulu voice.
//
// A TTS voice trained on isiZulu cannot pronounce "WhatsApp" - it has no such
// sounds in that order. Respelling rewrites the word into the host language
// orthography so the SAME voice can say it, instead of switching voices
// mid-sentence or reading it as noise.
//
// Port of Respeller.cs, LoanwordRespeller.cs, NguniRespeller.cs,
// PersonalRespellings.cs, IPhonemizer.cs and VoiceTrace.cs.

package com.bhengubv.circleai.voice

import java.time.Instant

/** A log line that can never take the caller down with it. */
object VoiceTrace {
    private val lock = Any()
    private var sink: ((String) -> Unit)? = null

    fun setSink(s: ((String) -> Unit)?) { synchronized(lock) { sink = s } }

    val enabled: Boolean get() = synchronized(lock) { sink != null }

    fun write(line: String) {
        val s = synchronized(lock) { sink }
        s?.invoke(line)
    }
}

/** Text in, phoneme units out. */
interface IPhonemizer {
    fun phonemize(text: String): List<String>
}

/** Splitting a phoneme string into units. */
object PiperPhonemes {
    /**
     * One code point per phoneme, skipping whitespace. Kotlin Char is a UTF-16
     * unit, the same as the C# char this was ported from, so the two agree
     * character for character.
     */
    fun split(s: String): List<String> =
        s.map { it.toString() }.filter { it.isNotBlank() }
}

/** The text IS already phonemes. For a caller that has done its own G2P. */
class PassthroughPhonemizer : IPhonemizer {
    override fun phonemize(text: String): List<String> =
        if (text.isEmpty()) emptyList() else PiperPhonemes.split(text)
}

/**
 * Whether a respelling is one people ALREADY write, or one this project is
 * proposing.
 *
 * The distinction matters: an attested form can ship silently, a proposed one
 * is a suggestion somebody may want to correct.
 */
enum class RespellingSource { ATTESTED, PROPOSED }

object LoanwordRespeller {

    /** Keyed case-insensitively; the values are the isiZulu spellings. */
    private val zulu: Map<String, Pair<String, RespellingSource>> = mapOf(
        "internet" to ("inthanethi" to RespellingSource.ATTESTED),
        "computer" to ("khompiyutha" to RespellingSource.ATTESTED),
        "phone" to ("foni" to RespellingSource.ATTESTED),
        "email" to ("imeyili" to RespellingSource.ATTESTED),
        "sms" to ("esemese" to RespellingSource.ATTESTED),
        "bank" to ("bhange" to RespellingSource.ATTESTED),
        "account" to ("akhawunti" to RespellingSource.ATTESTED),
        "station" to ("siteshi" to RespellingSource.ATTESTED),
        "radio" to ("umsakazo" to RespellingSource.ATTESTED),
        "taxi" to ("theksi" to RespellingSource.ATTESTED),
        "doctor" to ("dokotela" to RespellingSource.ATTESTED),
        "school" to ("sikole" to RespellingSource.ATTESTED),
        "whatsapp" to ("wotsapha" to RespellingSource.PROPOSED),
        "wifi" to ("wayifayi" to RespellingSource.PROPOSED),
        "gps" to ("jiphiyesi" to RespellingSource.PROPOSED),
        "youtube" to ("yuthubhu" to RespellingSource.PROPOSED),
        "google" to ("gugule" to RespellingSource.PROPOSED),
        "facebook" to ("feyisibhuku" to RespellingSource.PROPOSED),
        "airtime" to ("eyathayimu" to RespellingSource.PROPOSED),
        "data" to ("datha" to RespellingSource.PROPOSED),
        "atm" to ("eythiyemu" to RespellingSource.PROPOSED),
        "pin" to ("phini" to RespellingSource.PROPOSED),
        "circleai" to ("Sekhele Eyi Ayi" to RespellingSource.PROPOSED),
    )

    /**
     * Nothing is respelt for a language that does not need it - an English
     * voice saying an English word is already correct.
     */
    fun respell(word: String, hostLanguage: String): String? {
        if (word.isBlank() || !isNguniOrSotho(hostLanguage)) return null
        return zulu[word.lowercase()]?.first
    }

    fun source(word: String): RespellingSource? = zulu[word.lowercase()]?.second

    val known: List<String> get() = zulu.keys.toList()

    fun table(hostLanguage: String): Map<String, String> =
        if (!isNguniOrSotho(hostLanguage)) emptyMap() else zulu.mapValues { it.value.first }

    /**
     * The Nguni and Sotho-Tswana groups share the sound system this respelling
     * targets, so one table serves all of them.
     */
    fun isNguniOrSotho(tag: String): Boolean = when (tag.lowercase()) {
        "zu", "zul", "xh", "xho", "ss", "ssw", "nr", "nbl" -> true
        "st", "sot", "nso", "tn", "tsn" -> true
        else -> false
    }
}

/**
 * Turns an English IPA transcription into isiZulu orthography.
 *
 * The rule that does the work is VOWEL EPENTHESIS: Nguni syllables are open, so
 * a consonant cluster gets a vowel pushed between its parts and a word-final
 * consonant gets one after it. That is why "WhatsApp" comes out as something a
 * Zulu voice can actually say instead of a consonant pile-up.
 */
object NguniRespeller {

    private val consonants: Map<String, String> = mapOf(
        "p" to "ph", "b" to "b", "t" to "th", "d" to "d",
        "k" to "kh", "g" to "g", "m" to "m", "n" to "n",
        "ŋ" to "ng", "f" to "f", "v" to "v", "s" to "s",
        "z" to "z", "ʃ" to "sh", "ʒ" to "j", "h" to "h",
        "l" to "l", "r" to "r", "w" to "w", "j" to "y",
        "θ" to "th", "ð" to "d", "ʧ" to "tsh", "ʤ" to "j",
        "tʃ" to "tsh", "dʒ" to "j", "ɹ" to "r", "ɫ" to "l",
    )

    private val vowels: Map<String, String> = mapOf(
        "i" to "i", "ɪ" to "i", "iː" to "i", "e" to "e",
        "ɛ" to "e", "æ" to "a", "a" to "a", "ɑ" to "a",
        "ɑː" to "a", "ʌ" to "a", "ə" to "e", "ɜ" to "e",
        "ɜː" to "e", "ɒ" to "o", "ɔ" to "o", "ɔː" to "o",
        "o" to "o", "oʊ" to "o", "u" to "u", "ʊ" to "u",
        "uː" to "u", "aɪ" to "ayi", "aʊ" to "awu", "ɔɪ" to "oyi",
        "eɪ" to "eyi", "ɪə" to "iye", "eə" to "eya", "ʊə" to "uwa",
    )

    /**
     * The vowel epenthesis reaches for. Chosen because it is the least marked
     * in the language, so an inserted one reads as part of the word.
     */
    const val DEFAULT_VOWEL = "e"

    /** One parsed segment: the orthography it maps to, and whether it is a vowel. */
    data class Unit(val text: String, val isVowel: Boolean)

    fun fromIpa(ipa: String?): String {
        if (ipa == null || ipa.isBlank()) return ""

        val out = StringBuilder()
        var pendingConsonant = false
        for (unit in parse(ipa)) {
            if (unit.isVowel) {
                out.append(unit.text)
                pendingConsonant = false
                continue
            }
            // Two consonants in a row get a vowel between them...
            if (pendingConsonant) out.append(DEFAULT_VOWEL)
            out.append(unit.text)
            pendingConsonant = true
        }
        // ...and a word-final consonant gets one after it.
        if (pendingConsonant) out.append(DEFAULT_VOWEL)
        return out.toString()
    }

    /**
     * LONGEST MATCH FIRST, two symbols then one, so an affricate or a diphthong
     * is read as ONE unit rather than two.
     */
    fun parse(ipa: String): List<Unit> {
        val units = mutableListOf<Unit>()
        var i = 0

        while (i < ipa.length) {
            val c = ipa[i]
            // Stress marks, syllable dots, spaces, the tie bar and any combining
            // mark carry no segment of their own. The TIE BAR especially: it
            // joins an affricate, and treating it as a segment loses the
            // consonant it was joining.
            if (c == 'ˈ' || c == 'ˌ' || c == '.' || c == ' ' || c == '͡' ||
                Character.getType(c) == Character.NON_SPACING_MARK.toInt()
            ) {
                i++
                continue
            }

            var matched = false
            var len = minOf(2, ipa.length - i)
            while (len >= 1 && !matched) {
                val slice = ipa.substring(i, i + len)

                // A following length mark makes this a LONG vowel, which is a
                // different table entry, not the short one plus a stray colon.
                if (i + len < ipa.length && ipa[i + len] == 'ː' && vowels.containsKey(slice + "ː")) {
                    units.add(Unit(vowels[slice + "ː"]!!, true))
                    i += len + 1
                    matched = true
                } else if (vowels.containsKey(slice)) {
                    units.add(Unit(vowels[slice]!!, true))
                    i += len
                    matched = true
                } else if (consonants.containsKey(slice)) {
                    units.add(Unit(consonants[slice]!!, false))
                    i += len
                    matched = true
                }
                len--
            }
            // A symbol this does not model contributes NOTHING rather than
            // breaking the whole word.
            if (!matched) i++
        }
        return units
    }
}

data class LearnedWord(val word: String, val respelling: String, val learnedAt: Instant)

/**
 * What THIS person has corrected.
 *
 * A respelling somebody typed themselves outranks both the shipped table and
 * anything derived, because they know how their own name is said and this code
 * does not.
 */
class PersonalRespellings {

    private val lock = Any()
    private val learned = HashMap<String, LearnedWord>()

    fun learn(word: String, respelling: String, at: Instant = Instant.now()): Boolean {
        val w = word.trim()
        val r = respelling.trim()
        if (w.isEmpty() || r.isEmpty()) return false
        synchronized(lock) { learned[w.lowercase()] = LearnedWord(w, r, at) }
        return true
    }

    fun respell(word: String): String? = synchronized(lock) { learned[word.lowercase()]?.respelling }

    fun forget(word: String): Boolean = synchronized(lock) { learned.remove(word.lowercase()) != null }

    val all: List<LearnedWord>
        get() = synchronized(lock) { learned.values.toList() }.sortedBy { it.word }
}

/** A snapshot of what has been learned, for saving and restoring. */
data class LearningState(val words: List<LearnedWord>) {
    companion object {
        fun capture(p: PersonalRespellings): LearningState = LearningState(p.all)

        fun restore(state: LearningState): PersonalRespellings {
            val p = PersonalRespellings()
            for (w in state.words) p.learn(w.word, w.respelling, w.learnedAt)
            return p
        }
    }
}

/** Decides how one foreign word should be written so the host voice can say it. */
class Respeller(
    var hostLanguage: String = "",
    var personal: PersonalRespellings? = null,
    var englishPhonemizer: IPhonemizer? = null,
) {
    /**
     * THREE SOURCES, IN THIS ORDER, and the order is the whole design:
     *   1. what this person corrected  - they know their own words
     *   2. the attested table          - what people already write
     *   3. derived from English IPA    - a guess, and only for languages whose
     *                                    sound system this models
     *
     * Returns null when none applies, so the caller can fall back to spelling
     * the word out rather than mispronouncing it confidently.
     */
    fun respelling(word: String): String? {
        val w = word.trim()
        if (w.isEmpty()) return null

        personal?.respell(w)?.let { return it }
        LoanwordRespeller.respell(w, hostLanguage)?.let { return it }

        val phonemizer = englishPhonemizer ?: return null
        if (!LoanwordRespeller.isNguniOrSotho(hostLanguage)) return null

        val ipa = phonemizer.phonemize(w).joinToString("")
        val derived = NguniRespeller.fromIpa(ipa)
        if (derived.isBlank()) return null

        VoiceTrace.write("derived " + w + " -> " + derived + " (from " + ipa + ")")
        return derived
    }
}
