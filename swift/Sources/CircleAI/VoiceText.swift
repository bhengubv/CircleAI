// VoiceText.swift
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

import Foundation

// ── SentenceSplitter ─────────────────────────────────────────────────────────
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

/// One unit of speech, plus the silence that should follow it.
public struct SpeechSegment: Equatable, Sendable {
    /// The text to synthesise. Never empty or whitespace.
    public let text: String
    /// Silence to append after this segment, in milliseconds. 0 for the final
    /// segment — trailing silence at the end of a passage serves nothing.
    public let trailingPauseMs: Int

    public init(text: String, trailingPauseMs: Int) {
        self.text = text
        self.trailingPauseMs = trailingPauseMs
    }
}

public enum SentenceSplitter {
    // Pause lengths are the perceptual point of this type, so they are named
    // rather than buried. A full stop reads longer than a colon; a paragraph
    // break longer than either.
    private static let sentencePauseMs = 280
    private static let clausePauseMs = 200   // ':' and ';' — a lighter break
    private static let paragraphPauseMs = 400
    private static let forcedPauseMs = 60    // an over-long run cut for latency

    /// Beyond this many characters a segment is cut even without punctuation. A
    /// single unbroken clause of this size is already several seconds of audio,
    /// and on a phone the whole segment must render before ANY of it can play.
    /// The cut is taken at a word boundary and given only a token pause.
    public static let maxCharsPerSegment = 220

    /// Characters that end a sentence, across the scripts we speak.
    ///
    /// A Latin-only list silently under-splits every language that punctuates
    /// differently. Measured on the P30: Hindi, Bengali and Urdu produced THREE
    /// segments from the same five-sentence text that gave six in eleven other
    /// languages, because Devanagari and Bengali end sentences with the danda and
    /// Urdu with its own full stop — none of which were listed. The paragraph ran
    /// together exactly as it did before the splitter existed, for about a
    /// billion people, and nothing failed loudly enough to notice.
    private static let terminators: Set<UInt16> = Set(
        (".!?:;"                        // Latin / Cyrillic / Greek
         + "\u{0964}\u{0965}"           // danda, double danda — Devanagari, Bengali
         + "\u{06D4}\u{061F}\u{061B}"   // Arabic script — Urdu, Arabic, Persian
         + "\u{3002}\u{FF01}\u{FF1F}"   // CJK ideographic + fullwidth
         + "\u{FF0E}\u{FF1A}\u{FF1B}"   // fullwidth
         + "\u{1362}"                   // Ethiopic — Amharic, Tigrinya
         + "\u{17D4}"                   // Khmer khan
         + "\u{104A}\u{104B}"           // Myanmar little/section
        ).utf16)

    /// Terminators that can legitimately appear inside a token, and so need a
    /// following space before they may be read as ending a sentence.
    private static let mayOccurInsideAToken: Set<UInt16> = Set(".:;".utf16)

    private static let closers: Set<UInt16> = Set("\"')]".utf16)

    /// Splits `text` into segments. Returns a single segment when there is no
    /// sentence punctuation, and an empty array for blank input.
    ///
    /// INDEXED BY UTF-16 CODE UNIT, not by Character, because the reference walks
    /// a C# string. Every terminator in the table is in the BMP, so the two agree
    /// on where the splits fall — but `maxCharsPerSegment` counts units, and a
    /// port that counted grapheme clusters would cut over-long text elsewhere.
    public static func split(_ text: String?) -> [SpeechSegment] {
        var segments: [SpeechSegment] = []
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return segments
        }

        let units = Array(text.utf16)
        var current: [UInt16] = []
        let pending = sentencePauseMs

        for i in units.indices {
            let c = units[i]

            if c == 13 { continue }                 // '\r'
            if c == 10 {                            // '\n'
                current = flush(&segments, current, paragraphPauseMs)
                continue
            }

            current.append(c)

            if terminators.contains(c) && endsSentence(units, i) {
                let pause = (c == 58 || c == 59) ? clausePauseMs : sentencePauseMs
                current = flush(&segments, current, pause)
                continue
            }

            if current.count >= maxCharsPerSegment {
                current = cutAtWordBoundary(&segments, current)
            }
        }

        _ = flush(&segments, current, pending)

        // Nothing should follow the last word — a trailing pause is dead air.
        if !segments.isEmpty {
            segments[segments.count - 1] =
                SpeechSegment(text: segments[segments.count - 1].text, trailingPauseMs: 0)
        }

        return segments
    }

    /// True when the terminator at `i` really ends a sentence.
    ///
    /// A period between digits is a decimal ("3.5"), and one followed directly by
    /// a letter is usually an abbreviation or a URL — splitting there would cut a
    /// word in half and insert a pause inside it.
    private static func endsSentence(_ units: [UInt16], _ i: Int) -> Bool {
        // Absorb any run of closing punctuation ("...", "?!", ".").
        var j = i + 1
        while j < units.count && (terminators.contains(units[j]) || closers.contains(units[j])) {
            j += 1
        }

        if j >= units.count { return true }  // end of input

        // Only SOME terminators can appear inside a token — '.' in 3.5 and co.za,
        // ':' in 12:30. For those, a following space is what separates a sentence
        // end from a decimal point. The rest cannot occur mid-token in any script,
        // and demanding a space after them would never split Chinese, Japanese,
        // Khmer, Thai or Burmese at all: those scripts write without spaces
        // between words, so their full stop is followed by the next letter.
        if !mayOccurInsideAToken.contains(units[i]) { return true }

        if !isWhitespace(units[j]) { return false }  // 3.5, e.g., co.za

        if units[i] == 46, i > 0, isDigit(units[i - 1]),
           j + 1 < units.count, isDigit(units[j + 1]) {
            return false
        }

        return true
    }

    private static func isWhitespace(_ u: UInt16) -> Bool {
        guard let scalar = Unicode.Scalar(u) else { return false }
        return Character(scalar).isWhitespace
    }

    private static func isDigit(_ u: UInt16) -> Bool {
        u >= 48 && u <= 57
    }

    private static func flush(
        _ segments: inout [SpeechSegment], _ current: [UInt16], _ pauseMs: Int
    ) -> [UInt16] {
        let s = String(decoding: current, as: UTF16.self)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if s.isEmpty { return [] }

        // The terminator STAYS in the segment text, deliberately. It is tempting
        // to strip it — this type has already turned it into a pause, and the MMS
        // voices have no token for it. But the SA-11 voice's vocabulary DOES carry
        // '?' and '.', so it can render a real question rise that no inserted
        // silence could imitate. Stripping would have discarded that from all
        // eleven South African languages to tidy up a log line.

        // A segment of nothing but punctuation has no sound to make, and the
        // voice has no token for it either.
        if !s.contains(where: { $0.isLetter || $0.isNumber }) { return [] }

        segments.append(SpeechSegment(text: s, trailingPauseMs: pauseMs))
        return []
    }

    /// Cuts an over-long run at the last space, so the break lands between words
    /// rather than inside one. With no space to use the run is left intact — a
    /// mid-word cut would be audibly worse than a long segment.
    private static func cutAtWordBoundary(
        _ segments: inout [SpeechSegment], _ current: [UInt16]
    ) -> [UInt16] {
        guard let cut = current.lastIndex(of: 32), cut > 0 else { return current }

        let head = String(decoding: current[0..<cut], as: UTF16.self)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if !head.isEmpty {
            segments.append(SpeechSegment(text: head, trailingPauseMs: forcedPauseMs))
        }

        return Array(current[(cut + 1)...])
    }
}

// ── LanguageSpanSplitter ─────────────────────────────────────────────────────
//
// People do not speak one language per sentence. "Igama lami ngu-CircleAI" is
// isiZulu with an English name inside it, and read wholly in isiZulu the name
// comes out mangled — the listener hears the machine fail at a word they know
// perfectly well. A multi-lingual model takes ONE language id per utterance, so
// the fix is to cut the text where the language changes and synthesise each run
// under its own id.

/// A run of text to be spoken in one language.
public struct LanguageSpan: Equatable, Sendable {
    /// The words, with their spacing preserved.
    public let text: String
    /// True when this run is the embedded language (English), false for the
    /// surrounding one. The caller maps that to whatever ids its model uses.
    public let isForeign: Bool

    public init(text: String, isForeign: Bool) {
        self.text = text
        self.isForeign = isForeign
    }
}

public enum LanguageSpanSplitter {

    /// Splits `text` into spans. Returns a single span when the text is all one
    /// language, which is the overwhelmingly common case — callers can check
    /// `count == 1` and take their existing single-language path.
    public static func split(_ text: String?) -> [LanguageSpan] {
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return []
        }

        let chars = Array(text)
        var spans: [LanguageSpan] = []
        var current = ""
        var currentIsForeign: Bool?

        var i = 0
        while i < chars.count {
            // Separators (spaces, punctuation, the hyphen in "ngu-CircleAI") ride
            // along with whatever run they FOLLOW, so a language change never
            // strands a comma on its own or splits mid-punctuation.
            if !isLetterOrDigit(chars[i]) {
                let sepStart = i
                while i < chars.count && !isLetterOrDigit(chars[i]) { i += 1 }
                current += String(chars[sepStart..<i])
                continue
            }

            let wordStart = i
            while i < chars.count && isLetterOrDigit(chars[i]) { i += 1 }
            let word = String(chars[wordStart..<i])
            let foreign = isForeignWord(word)

            if let was = currentIsForeign, was != foreign {
                // The run ends at the last word, not at the separators that follow
                // it — those have already been appended and belong to the join.
                spans.append(LanguageSpan(text: current, isForeign: was))
                current = ""
            }

            currentIsForeign = foreign
            current += word
        }

        if !current.isEmpty, let was = currentIsForeign {
            spans.append(LanguageSpan(text: current, isForeign: was))
        }

        return spans
    }

    private static func isLetterOrDigit(_ c: Character) -> Bool {
        c.isLetter || c.isNumber
    }

    /// Rewrites a run into the form a voice can actually pronounce, without
    /// changing what is displayed.
    ///
    /// A compound like `CircleAI` is one token to a synthesiser and it has no idea
    /// where the words are, so it produces a mumble. Written `Circle AI` it is two
    /// things the voice already knows how to say. This is why the name came out
    /// garbled even after it was correctly switched to English — the language was
    /// right and the word was still unreadable.
    public static func toSpokenForm(_ text: String) -> String {
        if text.isEmpty { return text }

        let chars = Array(text)

        // 1. Break the compound into words at case boundaries, which is where the
        //    word boundaries genuinely are in this naming style.
        var spaced: [Character] = []
        for i in chars.indices {
            let c = chars[i]
            if i > 0 && c.isUppercase {
                let prev = chars[i - 1]
                let next: Character? = i + 1 < chars.count ? chars[i + 1] : nil

                // lower->Upper is a word boundary (Circle|AI, You|Tube).
                let afterLower = prev.isLowercase
                // Upper->Upper->lower ends a run of capitals (API|Key).
                let endOfAcronym = prev.isUppercase && (next?.isLowercase ?? false)

                if afterLower || endOfAcronym { spaced.append(" ") }
            }
            spaced.append(c)
        }

        // 2. Punctuate the acronyms. "AI" as a bare token gets read as a word —
        //    "ay" — where "A.I." is read as the letters, which is what it is. The
        //    full stops are for the voice, not the reader.
        var out = ""
        var i = 0
        while i < spaced.count {
            if !spaced[i].isUppercase {
                out.append(spaced[i])
                i += 1
                continue
            }

            let start = i
            while i < spaced.count && spaced[i].isUppercase { i += 1 }
            let run = spaced[start..<i]

            // A lone capital is an ordinary word opening ("Sawubona"), not an
            // acronym, and a run followed by lowercase was already split above.
            if run.count < 2 {
                out += String(run)
                continue
            }

            for ch in run { out.append(ch); out.append(".") }
        }
        return out
    }

    /// Is this token unmistakably foreign (English) inside African-language text?
    ///
    /// Two signals only, both chosen because native orthographies do not produce
    /// them:
    ///
    ///   internal capitals     — CircleAI, WhatsApp, MTN's brand spellings
    ///   all-caps, 2-5 letters — GPS, SMS, ATM, PIN
    ///
    /// isiZulu, isiXhosa, Sesotho and the rest capitalise the first letter of a
    /// sentence or a proper noun and nothing else, so neither pattern arises
    /// naturally. A sentence-initial capital is therefore NOT a signal, which is
    /// why only capitals after position zero count.
    ///
    /// It does NOT try to spot ordinary lowercase English words like "computer" —
    /// that needs a lexicon per language pair, and guessing wrong is worse than
    /// not guessing: mispronouncing a native word to "fix" a foreign one insults
    /// the speaker in their own language.
    public static func isForeignWord(_ word: String) -> Bool {
        let chars = Array(word)
        if chars.count < 2 { return false }

        var upper = 0
        var lower = 0
        var hasInternalCapital = false

        for (i, c) in chars.enumerated() {
            if !c.isLetter { continue }
            if c.isUppercase {
                upper += 1
                if i > 0 { hasInternalCapital = true }
            } else {
                lower += 1
            }
        }

        if hasInternalCapital && lower > 0 { return true }             // CircleAI
        if upper >= 2 && lower == 0 && chars.count <= 5 { return true } // GPS, SMS
        return false
    }
}

// ── GeezRomanizer ────────────────────────────────────────────────────────────
//
// Ethiopic (Ge'ez) script → Latin, because the Amharic and Tigrinya voices do
// not read Ethiopic at all. Meta ships those two MMS models with
// `is_uroman: true`: their vocabularies are 28 and 27 LATIN letters and they
// expect text already transliterated. Measured on the P30, Amharic lost 43
// distinct characters and produced 3.2 s of noise for a 15 s paragraph.
//
// The transliteration is computed, not tabulated, because Unicode lays the
// syllabary out exactly as the script is taught: each consecutive block of EIGHT
// codepoints is one consonant across its vowel orders.

public enum GeezRomanizer {
    private static let base = 0x1200
    private static let ordersPerConsonant = 8

    /// Last codepoint that follows the eight-orders-per-consonant layout. The
    /// syllabary ends here; everything above is lone syllables, marks and
    /// numerals, and treating any of it as a row invents a pronunciation.
    private static let lastSyllable = 0x1357

    /// Consonant per 8-codepoint row, in Unicode order. ASCII only: these voices
    /// hold 27-28 plain Latin letters, so a transliteration carrying the Ethiopist
    /// diacritics would be dropped as surely as the Ethiopic was.
    ///
    /// Six rows are LABIALISED — the consonant carries a built-in /w/. Writing
    /// them plain turns "kwa" into "ka", which silently changes the word.
    private static let consonants = [
        "h", "l", "h", "m", "s", "r", "s", "sh",
        "q", "qw", "q", "qw", "b", "v", "t", "ch",
        "h", "hw", "n", "ny", "", "k", "kw", "k",
        "kw", "w", "", "z", "zh", "y", "d", "d",
        "j", "g", "gw", "ng", "t", "ch", "p", "ts",
        "ts", "f", "p",
    ]

    /// Vowel per order. The sixth is SILENT — it marks a bare consonant, which is
    /// why the greeting romanises with no trailing vowel.
    private static let vowels = ["e", "u", "i", "a", "e", "", "o", "wa"]

    /// The three syllables Unicode assigns singly rather than as a row of eight.
    /// They are already in the -a order, so the vowel is part of the value.
    private static let loneSyllables: [Int: String] = [
        0x1358: "rya",
        0x1359: "mya",
        0x135A: "fya",
    ]

    /// Combining marks. They modify the syllable before them and have no sound of
    /// their own, so they are dropped rather than passed through — a bare mark
    /// reaching a Latin-only vocabulary is one more unmapped symbol.
    private static let marks: Set<Int> = [0x135D, 0x135E, 0x135F]

    /// Ethiopic punctuation, mapped so sentence splitting still works.
    private static let punctuation: [Int: String] = [
        0x1360: " ",   // section
        0x1361: " ",   // word separator
        0x1362: ".",   // full stop
        0x1363: ",",   // comma
        0x1364: ";",   // semicolon
        0x1365: ":",   // colon
        0x1366: ":",   // preface colon
        0x1367: "?",   // question mark
        0x1368: " ",   // paragraph separator
    ]

    /// True when `text` contains any Ethiopic character.
    public static func isEthiopic(_ text: String?) -> Bool {
        guard let text, !text.isEmpty else { return false }
        return text.unicodeScalars.contains { $0.value >= 0x1200 && $0.value <= 0x139F }
    }

    /// Ethiopic → Latin. Characters outside the script pass through untouched, so
    /// mixed text (numerals, Latin names, punctuation) survives intact.
    public static func romanize(_ text: String?) -> String {
        guard let text, !text.isEmpty else { return text ?? "" }

        var out = ""
        for scalar in text.unicodeScalars {
            let cp = Int(scalar.value)

            if let p = punctuation[cp] { out += p; continue }

            // THE EIGHT-PER-CONSONANT LAYOUT STOPS AT U+1357, and the range check
            // has to stop with it. Beyond that the block is no longer a syllabary:
            // U+1358..U+135A are three LONE syllables already in their -a order,
            // U+135D..U+135F are combining marks, and U+1369 onward are the
            // numerals. Sizing the check off the consonant table instead swept
            // seven of those numerals back into the syllabary — and they came out
            // as sound, so nothing failed.
            if marks.contains(cp) { continue }
            if let lone = loneSyllables[cp] { out += lone; continue }

            let i = cp - base
            if i < 0 || i > lastSyllable - base {
                // Numerals and the rarely-used supplement blocks have no sound we
                // can render; anything else is not Ethiopic and is left alone.
                if cp >= 0x1369 && cp <= 0x137C { continue }
                out.unicodeScalars.append(scalar)
                continue
            }

            let row = i / ordersPerConsonant
            let order = i % ordersPerConsonant

            let consonant = consonants[row]
            var vowel = vowels[order]

            if consonant.isEmpty {
                // The glottal and pharyngeal rows write no consonant in Latin, so
                // the vowel IS the character. First order is heard as "a", and the
                // sixth — silent after a real consonant — must still sound here, or
                // the word-initial one disappears entirely.
                if order == 0 { vowel = "a" } else if vowel.isEmpty { vowel = "e" }
            }

            out += consonant + vowel
        }
        return out
    }
}

// ── ToneShaper ───────────────────────────────────────────────────────────────
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

/// Biquad coefficients, already normalised by a0.
public struct BiquadCoefficients: Sendable {
    public let b: [Double]
    public let a: [Double]

    public init(b: [Double], a: [Double]) {
        self.b = b
        self.a = a
    }
}

public struct ToneShaperSettings: Equatable, Sendable {
    /// Where the low shelf starts lifting, in Hz.
    public let lowShelfHz: Double
    /// How much to lift the bottom, in dB.
    public let lowShelfDb: Double
    /// Centre of the harshness dip, in Hz.
    public let presenceHz: Double
    /// How much to cut there, in dB. Negative cuts.
    public let presenceDb: Double
    /// Width of the dip. Lower is wider.
    public let presenceQ: Double

    public init(
        lowShelfHz: Double = 320, lowShelfDb: Double = 4.0,
        presenceHz: Double = 3200, presenceDb: Double = -4.0, presenceQ: Double = 0.8
    ) {
        self.lowShelfHz = lowShelfHz
        self.lowShelfDb = lowShelfDb
        self.presenceHz = presenceHz
        self.presenceDb = presenceDb
        self.presenceQ = presenceQ
    }
}

public enum ToneShaper {
    /// The measured setting: warmer, with no cost to intelligibility.
    public static let warm = ToneShaperSettings()

    private static let lowShelfSlope = 0.9

    private static func normalise(_ b: [Double], _ a: [Double]) -> BiquadCoefficients {
        let a0 = a[0]
        return BiquadCoefficients(b: b.map { $0 / a0 }, a: a.map { $0 / a0 })
    }

    /// RBJ audio-cookbook low shelf, normalised by a0.
    public static func lowShelf(_ s: ToneShaperSettings, rate: Int) -> BiquadCoefficients {
        let amp = pow(10, s.lowShelfDb / 40)
        let w0 = 2 * Double.pi * s.lowShelfHz / Double(rate)
        let alpha = sin(w0) / 2 * (((amp + 1 / amp) * (1 / lowShelfSlope - 1) + 2)).squareRoot()
        let c = cos(w0)
        let s2 = 2 * amp.squareRoot() * alpha

        return normalise(
            [amp * ((amp + 1) - (amp - 1) * c + s2),
             2 * amp * ((amp - 1) - (amp + 1) * c),
             amp * ((amp + 1) - (amp - 1) * c - s2)],
            [(amp + 1) + (amp - 1) * c + s2,
             -2 * ((amp - 1) + (amp + 1) * c),
             (amp + 1) + (amp - 1) * c - s2]
        )
    }

    /// RBJ audio-cookbook peaking EQ, normalised by a0.
    public static func peaking(_ s: ToneShaperSettings, rate: Int) -> BiquadCoefficients {
        let amp = pow(10, s.presenceDb / 40)
        let w0 = 2 * Double.pi * s.presenceHz / Double(rate)
        let alpha = sin(w0) / (2 * s.presenceQ)
        let c = cos(w0)

        return normalise(
            [1 + alpha * amp, -2 * c, 1 - alpha * amp],
            [1 + alpha / amp, -2 * c, 1 - alpha / amp]
        )
    }

    /// Direct-form-I biquad, in place.
    ///
    /// THE STATE IS DOUBLE AND THE STORED SAMPLE IS FLOAT, and both halves matter.
    /// The filter memory never sees the float rounding — y1 keeps the
    /// full-precision result — so the recursion is identical everywhere. Only what
    /// lands in the buffer is narrowed, which is what the next stage then reads.
    public static func biquad(_ x: inout [Float], _ c: BiquadCoefficients) {
        var x1 = 0.0, x2 = 0.0, y1 = 0.0, y2 = 0.0
        for i in x.indices {
            let xn = Double(x[i])
            let yn = c.b[0] * xn + c.b[1] * x1 + c.b[2] * x2 - c.a[1] * y1 - c.a[2] * y2
            x2 = x1; x1 = xn
            y2 = y1; y1 = yn
            x[i] = Float(yn)
        }
    }

    private static func peak(_ x: [Float]) -> Float {
        var p: Float = 0
        for v in x { let a = abs(v); if a > p { p = a } }
        return p
    }

    /// Filters `waveform` in place with a low shelf and a presence dip in series.
    ///
    /// PEAK IS RESTORED AFTERWARDS. Lifting the low shelf adds energy, and a
    /// waveform that already peaked near full scale would clip — which is heard as
    /// crackle and would be blamed on the quantised model rather than on this.
    /// Scaling back to the original peak keeps the tone change audible and the
    /// level unchanged.
    public static func apply(
        _ waveform: inout [Float], sampleRate: Int, settings: ToneShaperSettings = warm
    ) {
        if waveform.isEmpty || sampleRate <= 0 { return }

        let before = peak(waveform)
        if before <= 0 { return }  // a silent buffer, and dividing by that peak is NaN

        biquad(&waveform, lowShelf(settings, rate: sampleRate))
        biquad(&waveform, peaking(settings, rate: sampleRate))

        let after = peak(waveform)
        if after > 0 && after > before {
            // Float division, because the reference divides two FLOATS here.
            // Widening to double makes the gain a few ULP different and the whole
            // tail of the waveform drifts with it.
            let g = before / after
            for i in waveform.indices { waveform[i] *= g }
        }
    }
}

// ── NchltPhonemizer ──────────────────────────────────────────────────────────
//
// A fully sovereign, permissive-licence grapheme-to-phoneme front-end for the
// South African languages. NOT espeak-ng (GPLv3 taints the app), NOT phonemeza
// (unlicensed, weights unpublished), and not neural. A faithful port of the
// NCHLT pronunciation predictor (Marelie Davel, pron_predict.pl) driven by the
// NCHLT-inlang resources, © DAC / CSIR / NWU under CC BY 3.0.
//
// Because the rule set covers any word there is no "OOV gap": a word is either
// in the dictionary (exact) or synthesised by the rules, which is what makes
// agglutinative isiZulu tractable.

public final class NchltPhonemizer {
    /// One context rule: grapheme `g` in left/right context → code.
    private struct Rule {
        let order: Int
        let left: String
        let right: String
        let code: String
    }

    private let dict: [String: [String]]
    private let rules: [Character: [Rule]]
    private let phoneMap: [Character: String]
    private let graphMap: [Character: Character]
    private let gnulls: [(String, String)]

    /// Words in the last `phonemize` call that were synthesised by the rule engine
    /// rather than found in the dictionary. A coverage diagnostic, never a failure
    /// — the rules always produce output.
    public private(set) var lastRulePredictedWords = 0

    /// Graphemes in the last call that no rule covered. Skipped, never guessed.
    public private(set) var lastUnknownGraphemes: [String] = []

    private init(
        dict: [String: [String]], rules: [Character: [Rule]],
        phoneMap: [Character: String], graphMap: [Character: Character],
        gnulls: [(String, String)]
    ) {
        self.dict = dict
        self.rules = rules
        self.phoneMap = phoneMap
        self.graphMap = graphMap
        self.gnulls = gnulls
    }

    /// Build from the file CONTENTS rather than paths, so a caller can load from
    /// an embedded resource or a downloaded bundle with no filesystem in reach.
    public static func fromText(
        dict dictText: String, rules rulesText: String, phoneMap phoneMapText: String,
        graphMap graphMapText: String? = nil, gnulls gnullsText: String? = nil
    ) -> NchltPhonemizer {
        NchltPhonemizer(
            dict: parseDict(dictText),
            rules: parseRules(rulesText),
            phoneMap: parsePhoneMap(phoneMapText),
            graphMap: (graphMapText?.isEmpty ?? true) ? [:] : parseGraphMap(graphMapText!),
            gnulls: (gnullsText?.isEmpty ?? true) ? [] : parseGnulls(gnullsText!)
        )
    }

    public func phonemize(_ text: String) -> [String] {
        lastRulePredictedWords = 0
        lastUnknownGraphemes = []
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return [] }

        var phones: [String] = []
        for word in NchltPhonemizer.tokenize(text) {
            if let known = dict[word] {
                phones += known
            } else {
                phones += predictWord(word)
                lastRulePredictedWords += 1
            }
        }
        return phones
    }

    /// Predict a single word's X-SAMPA phones from the context rules — the exact
    /// algorithm of `g2p_word_olist`: for each grapheme take the highest-order
    /// rule whose left/right context matches, emit its code, drop nulls, then
    /// remap codes to X-SAMPA.
    ///
    /// Does NOT clear the unknown-grapheme list, matching the reference:
    /// `phonemize` owns the reset, so a direct call accumulates rather than hiding
    /// what an earlier word already reported.
    public func predictWord(_ word: String) -> [String] {
        if word.isEmpty { return [] }

        // Grapheme remap (usually identity) then grapheme-null insertion.
        let w = Array(applyGnulls(mapGraphemes(word)))

        var codes: [Character] = []
        for i in w.indices {
            let g = w[i]
            guard let gRules = rules[g] else {
                // Skip an unknown grapheme rather than fabricate a phone for it.
                let s = String(g)
                if !lastUnknownGraphemes.contains(s) { lastUnknownGraphemes.append(s) }
                continue
            }

            // pat = " " + left-context + "-" + g + "-" + right-context + " "
            let pat = " " + String(w[0..<i]) + "-" + String(g) + "-"
                + String(w[(i + 1)...]) + " "

            // Rules are pre-sorted most-specific-first; the first match wins.
            var code: Character = "0"
            for r in gRules where pat.contains(r.left + "-" + String(g) + "-" + r.right) {
                code = r.code.first ?? "0"
                break
            }
            if code != "0" { codes.append(code) }
        }

        return codes.map { phoneMap[$0] ?? String($0) }
    }

    private func mapGraphemes(_ word: String) -> String {
        if graphMap.isEmpty { return word }
        return String(word.map { graphMap[$0] ?? $0 })
    }

    private func applyGnulls(_ word: String) -> String {
        var w = word
        for (from, to) in gnulls { w = w.replacingOccurrences(of: from, with: to) }
        return w
    }

    /// Lower-case and split into word tokens on anything that is not a letter.
    /// Diacritics are preserved (Afrikaans ê/ë/ô are real graphemes); digits and
    /// punctuation become separators. Number and abbreviation expansion is out of
    /// scope and belongs to a text-normalisation pass upstream.
    private static func tokenize(_ text: String) -> [String] {
        var words: [String] = []
        var sb = ""
        for ch in text.trimmingCharacters(in: .whitespacesAndNewlines) {
            if ch.isLetter {
                sb += String(ch).lowercased()
            } else if !sb.isEmpty {
                words.append(sb)
                sb = ""
            }
        }
        if !sb.isEmpty { words.append(sb) }
        return words
    }

    /// Split the way a StreamReader does, so a CRLF file parses identically.
    private static func lines(_ text: String) -> [String] {
        text.components(separatedBy: "\n").map {
            $0.hasSuffix("\r") ? String($0.dropLast()) : $0
        }
    }

    private static func parseDict(_ text: String) -> [String: [String]] {
        var dict: [String: [String]] = [:]
        for line in lines(text) {
            if line.isEmpty { continue }
            guard let tab = line.firstIndex(of: "\t"), tab != line.startIndex else { continue }
            let word = String(line[line.startIndex..<tab])
            let pron = String(line[line.index(after: tab)...])
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if pron.isEmpty || dict[word] != nil { continue }  // keep the FIRST variant
            dict[word] = pron.split(separator: " ").map(String.init)
        }
        return dict
    }

    private static func parseRules(_ text: String) -> [Character: [Rule]] {
        var byGrapheme: [Character: [Rule]] = [:]
        // Insertion order per grapheme, so the stable sort below has something
        // real to preserve.
        for line in lines(text) {
            if line.isEmpty { continue }
            // grapheme ; left ; right ; code ; order [ ; count ]
            let f = line.components(separatedBy: ";")
            if f.count < 5 || f[0].isEmpty { continue }
            guard let order = Int(f[4].trimmingCharacters(in: .whitespaces)) else { continue }
            byGrapheme[f[0].first!, default: []]
                .append(Rule(order: order, left: f[1], right: f[2], code: f[3]))
        }

        // STABLE sort, descending by order. Two rules of equal order must stay in
        // file order — the reference uses LINQ's OrderByDescending, which is
        // stable, and Swift's sort is NOT, so sorting on (order, index) is the
        // only way to keep ties in the order the file gave them.
        var sorted: [Character: [Rule]] = [:]
        for (g, list) in byGrapheme {
            sorted[g] = list.enumerated()
                .sorted { $0.element.order != $1.element.order
                    ? $0.element.order > $1.element.order
                    : $0.offset < $1.offset }
                .map(\.element)
        }
        return sorted
    }

    private static func parsePhoneMap(_ text: String) -> [Character: String] {
        // Line: "<code>\t<xsampa>"  (code is a single char).
        var map: [Character: String] = [:]
        for line in lines(text) {
            if line.isEmpty { continue }
            guard let tab = line.firstIndex(of: "\t"), tab != line.startIndex else { continue }
            let code = String(line[line.startIndex..<tab])
            if code.count == 1 {
                map[code.first!] = String(line[line.index(after: tab)...])
            }
        }
        return map
    }

    private static func parseGraphMap(_ text: String) -> [Character: Character] {
        // File line: "<funny>\t<std>" — we map std->funny (per remap_dict's gmap).
        var map: [Character: Character] = [:]
        for line in lines(text) {
            if line.isEmpty { continue }
            let f = line.components(separatedBy: "\t")
            if f.count == 2, f[0].count == 1, f[1].count == 1, f[0].first! != f[1].first! {
                map[f[1].first!] = f[0].first!
            }
        }
        return map
    }

    private static func parseGnulls(_ text: String) -> [(String, String)] {
        // File line: "<from>;<to>" — insert grapheme-nulls (empty for Nguni).
        var list: [(String, String)] = []
        for line in lines(text) {
            if line.isEmpty { continue }
            let f = line.components(separatedBy: ";")
            if f.count == 2 { list.append((f[0], f[1])) }
        }
        return list
    }
}
